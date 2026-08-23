using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenBurn.AI;
using OpenBurn.Cam;
using OpenBurn.Cam.Import;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Jobs;
using OpenBurn.GCode;

namespace OpenBurn.App.ViewModels;

/// <summary>
/// The shell's side of the assistant contract.
///
/// The important part of this file is what it does <em>not</em> expose. Reads are
/// unrestricted; writes are limited to layer settings and preparing a test grid,
/// both reversible and neither of which starts anything. Every request that would
/// move the gantry or fire the beam goes through <see cref="ProposeAction"/> and
/// becomes a card the operator has to click. There is no code path from the
/// assistant to the machine, which is a stronger guarantee than any instruction
/// in a prompt.
/// </summary>
public sealed partial class MainViewModel : IAssistantHost
{
    [ObservableProperty]
    private bool _showAssistant;

    private AssistantViewModel? _assistant;

    public AssistantViewModel Assistant => _assistant ??= new AssistantViewModel(this);

    [RelayCommand]
    private void ToggleAssistant()
    {
        ShowAssistant = !ShowAssistant;
        if (ShowAssistant) OnPropertyChanged(nameof(Assistant));
    }

    /// <summary>Hand the current fault to the assistant with the console log attached.</summary>
    [RelayCommand]
    private async Task DiagnoseFaultAsync()
    {
        if (LastFault is not { } fault) return;
        ShowAssistant = true;
        await Assistant.DiagnoseAsync(
            $"{(LastFaultIsAlarm ? "ALARM" : "error")}:{fault.Code} — {fault.Title}. {fault.Message}").ConfigureAwait(true);
    }

    // --------------------------------------------------------- IAssistantHost

    public AssistantContext BuildContext()
    {
        var layers = Layers.Select(l => new LayerSummary(
            l.Name,
            l.Operation.ToString(),
            l.SpeedMmMin,
            l.PowerPercent,
            l.Passes,
            l.LineIntervalMm,
            l.Enabled,
            Design.Shapes.Count(s => s.LayerId == l.Layer.Id))).ToList();

        JobSummary? job = null;
        if (_cam is { } cam)
        {
            job = new JobSummary(
                cam.Job.LineCount,
                cam.Job.Bounds.IsEmpty ? 0 : cam.Job.Bounds.Width,
                cam.Job.Bounds.IsEmpty ? 0 : cam.Job.Bounds.Height,
                cam.Toolpath.CutLengthMm,
                cam.Toolpath.TravelLengthMm,
                TimeEstimator.Format(cam.Estimate.Total),
                [.. cam.Issues.Select(i => $"{i.Severity}: {i.Title} — {i.Detail}")]);
        }

        return new AssistantContext
        {
            MachineName = SelectedMachine.DisplayName,
            LaserWatts = SelectedMachine.LaserWatts,
            BedWidthMm = SelectedMachine.BedWidthMm,
            BedHeightMm = SelectedMachine.BedHeightMm,
            IsConnected = IsConnected,
            MachineState = MachineStateName,
            IsHomed = IsHomed,
            WorkPosition = IsConnected ? WorkPositionText : null,
            ControllerSettings = _device?.Settings,
            Layers = layers,
            Job = job,
            ConsoleTail = Console.Tail(60),
            SelectedMaterial = SelectedMaterial?.FullPath,
        };
    }

    public string ApplyLayerChanges(IReadOnlyList<LayerChange> changes)
    {
        var applied = new List<string>();

        foreach (var change in changes)
        {
            var layer = Layers.FirstOrDefault(l =>
                string.Equals(l.Name, change.LayerName, StringComparison.OrdinalIgnoreCase));

            if (layer is null)
            {
                applied.Add($"No layer called '{change.LayerName}'. Layers are: {string.Join(", ", Layers.Select(l => l.Name))}.");
                continue;
            }

            var parts = new List<string>();

            if (change.SpeedMmMin is { } speed && speed > 0)
            {
                var clamped = Math.Min(speed, SelectedMachine.MaxSpeedMmMin);
                if (clamped < speed) parts.Add($"speed {speed:0}→{clamped:0} mm/min (machine maximum)");
                else parts.Add($"speed {clamped:0} mm/min");
                layer.SpeedMmMin = clamped;
            }

            if (change.PowerPercent is { } power)
            {
                layer.PowerPercent = Math.Clamp(power, 0, 100);
                parts.Add($"power {layer.PowerPercent:0.#}%");
            }

            if (change.Passes is { } passes)
            {
                layer.Passes = Math.Max(1, passes);
                parts.Add($"{layer.Passes} pass(es)");
            }

            if (change.LineIntervalMm is { } interval && interval > 0)
            {
                layer.LineIntervalMm = interval;
                parts.Add($"interval {layer.LineIntervalMm:0.###} mm");
            }

            if (change.AirAssist is { } air)
            {
                layer.AirAssist = air;
                parts.Add(air ? "air assist on" : "air assist off");
            }

            if (parts.Count > 0) applied.Add($"{layer.Name}: {string.Join(", ", parts)}");
        }

        QueueRegenerate();

        var summary = applied.Count == 0 ? "Nothing was changed." : string.Join("; ", applied);
        Console.AppendInfo($"Assistant changed layer settings — {summary}");
        return summary;
    }

    public string PrepareTestGrid(double[] powers, double[] speeds, double cellSizeMm)
    {
        var grid = CamPipeline.GenerateTestGrid(
            SelectedMachine,
            powers,
            speeds,
            cellSizeMm <= 0 ? 8 : cellSizeMm);

        _pendingAssistantJob = grid.Job;

        var blocking = grid.Issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();
        if (blocking.Count > 0)
        {
            _pendingAssistantJob = null;
            return "The grid does not fit the bed: " + string.Join("; ", blocking.Select(i => i.Detail));
        }

        Console.AppendInfo($"Assistant prepared a {powers.Length}×{speeds.Length} test grid " +
                           $"({TimeEstimator.Format(grid.Estimate.Total)} estimated). It has not been started.");

        return $"Prepared a {powers.Length}×{speeds.Length} grid, " +
               $"{grid.Job.Bounds.Width:0.#}×{grid.Job.Bounds.Height:0.#} mm, " +
               $"estimated {TimeEstimator.Format(grid.Estimate.Total)}. " +
               "It is loaded but NOT running — the operator must press Start.";
    }

    private JobDefinition? _pendingAssistantJob;

    /// <summary>
    /// Turn an assistant proposal into a card the operator must click.
    ///
    /// Note what happens here: the proposal is stored and displayed. It is not
    /// executed. The lambda that would execute it only runs from a button press.
    /// </summary>
    public void ProposeAction(ProposedAction action)
    {
        Assistant.AddPendingAction(action.Kind, action.Description, () => RunProposedAsync(action));
    }

    private async Task RunProposedAsync(ProposedAction action)
    {
        Console.AppendInfo($"Operator confirmed assistant proposal: {action.Description}");

        switch (action.Kind)
        {
            case "home":
                await HomeAsync().ConfigureAwait(true);
                break;

            case "unlock":
                await UnlockAsync().ConfigureAwait(true);
                break;

            case "frame":
                await FrameAsync().ConfigureAwait(true);
                break;

            case "set_origin":
                await ZeroAllAsync().ConfigureAwait(true);
                break;

            case "jog":
            {
                var dx = ParseParameter(action, "x");
                var dy = ParseParameter(action, "y");
                if (dx == 0 && dy == 0) break;
                await GuardAsync(d => d.JogAsync(dx, dy, 0, JogFeedMmMin), "Jog").ConfigureAwait(true);
                break;
            }

            case "start_job":
            {
                if (_pendingAssistantJob is { } prepared)
                {
                    await GuardAsync(d => d.StartJobAsync(prepared), "Starting the prepared job").ConfigureAwait(true);
                    _pendingAssistantJob = null;
                }
                else
                {
                    await StartJobAsync().ConfigureAwait(true);
                }
                break;
            }

            default:
                Console.AppendError($"The assistant proposed an action OpenBurn does not know how to perform: {action.Kind}");
                break;
        }
    }

    private static double ParseParameter(ProposedAction action, string key) =>
        action.Parameters.TryGetValue(key, out var raw) &&
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    public byte[]? GetSelectedImagePng() =>
        PrimarySelection is ImageShape image ? ImageImporter.ToPng(image.Source) : null;
}
