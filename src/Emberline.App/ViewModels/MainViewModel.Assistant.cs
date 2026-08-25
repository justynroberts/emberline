using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emberline.AI;
using Emberline.Core.Geometry;
using Emberline.Cam;
using Emberline.Cam.Import;
using Emberline.Core.Documents;
using Emberline.Core.Jobs;
using Emberline.GCode;

namespace Emberline.App.ViewModels;

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

    /// <summary>
    /// Largest SVG the assistant may hand over. Generous for real artwork and far
    /// below anything that would take a noticeable time to parse.
    /// </summary>
    private const int MaxAssistantSvgBytes = 512 * 1024;

    /// <summary>
    /// Put drawn artwork on the bed.
    ///
    /// This is a document change, not a machine action, so it does not go through
    /// the confirmation gate: nothing moves and nothing burns, it appears on the
    /// canvas exactly as an opened file would, and Ctrl+Z removes it. The gate
    /// exists for the laser, and widening it to cover harmless edits would train
    /// people to dismiss confirmations without reading them.
    ///
    /// What is checked here is that the SVG is sane: bounded in size, parseable,
    /// non-empty, and small enough to sit on the bed.
    /// </summary>
    public string AddArtwork(string svg, string name)
    {
        if (string.IsNullOrWhiteSpace(svg)) return "Nothing was supplied.";

        if (svg.Length > MaxAssistantSvgBytes)
        {
            return $"That SVG is {svg.Length / 1024} KB, over the {MaxAssistantSvgBytes / 1024} KB limit. Send something simpler.";
        }

        Cam.Import.SvgImportResult result;
        try
        {
            result = SvgImporter.Import(svg);
        }
        catch (Exception ex)
        {
            return $"That SVG could not be read: {ex.Message}";
        }

        if (result.Paths.Count == 0)
        {
            return "That SVG produced no paths. A laser follows outlines — use stroked shapes with fill=\"none\" rather than filled regions.";
        }

        var shape = new PathShape(result.Paths) { Name = string.IsNullOrWhiteSpace(name) ? "Drawing" : name.Trim() };
        var size = shape.LocalBounds;

        if (size.IsEmpty || size.Width <= 0.01 || size.Height <= 0.01)
        {
            return "That artwork has no size. Give the SVG a width, a height and a matching viewBox, in millimetres.";
        }

        if (size.Width > SelectedMachine.BedWidthMm * 4 || size.Height > SelectedMachine.BedHeightMm * 4)
        {
            return $"That artwork is {size.Width:0} × {size.Height:0} mm, far larger than the " +
                   $"{SelectedMachine.BedWidthMm:0} × {SelectedMachine.BedHeightMm:0} mm bed. Check the SVG units.";
        }

        // Land it on the workpiece when there is one, since that is what the
        // operator is actually burning onto.
        var target = Design.Workpiece.IsSet
            ? Design.Workpiece.Bounds.Center
            : new Vec2(SelectedMachine.BedWidthMm / 2, SelectedMachine.BedHeightMm / 2);

        shape.Translate(new Vec2(target.X - size.Center.X, target.Y - size.Center.Y));

        EditDocument($"Draw {shape.Name}", () =>
        {
            Design.AddShape(shape, SelectedLayer?.Layer);
            Selection.Clear();
            Selection.Add(shape);
        });

        var placed = Design.Workpiece.IsSet ? "centred on the workpiece" : "centred on the bed";
        var warnings = result.Warnings.Count > 0 ? " " + string.Join(" ", result.Warnings) : "";

        Console.AppendInfo($"The assistant drew “{shape.Name}” — {result.Paths.Count} path(s), " +
                           $"{size.Width:0.#} × {size.Height:0.#} mm, {placed}. Undo removes it.");

        return $"Added “{shape.Name}”: {result.Paths.Count} path(s), {size.Width:0.#} × {size.Height:0.#} mm, {placed}. " +
               $"It is selected on the canvas and can be moved, resized or undone.{warnings}";
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
                Console.AppendError($"The assistant proposed an action Emberline does not know how to perform: {action.Kind}");
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
