using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Input.Platform;
using OpenBurn.Core.Units;
using OpenBurn.GCode;

namespace OpenBurn.App.ViewModels;

/// <summary>
/// Toolpath simulation and G-code inspection.
///
/// The PRD asks that generated G-code always be inspectable before execution, and
/// that a preview identify problems before a workpiece is committed. Both live
/// here: a replay that uses the same acceleration model as the estimate, and a
/// text view of the exact lines that will be sent.
/// </summary>
public sealed partial class MainViewModel
{
    private ToolpathSimulator? _simulator;
    private DispatcherTimer? _playback;
    private DateTimeOffset _playbackStarted;
    private TimeSpan _playbackOffset;

    [ObservableProperty]
    private bool _showGcode;

    [ObservableProperty]
    private bool _isSimulating;

    [ObservableProperty]
    private double _simulationFraction;

    [ObservableProperty]
    private double _simulationSpeed = 8;

    [ObservableProperty]
    private string _simulationStatus = "Not running";

    /// <summary>
    /// Whether the simulation bar is on screen.
    ///
    /// Explicit state rather than derived from the position, because the bar
    /// contains the scrub slider that writes that position back — deriving
    /// visibility from it lets the control dismiss its own container.
    /// </summary>
    [ObservableProperty]
    private bool _simulationVisible;

    public IReadOnlyList<double> SimulationSpeeds { get; } = [1, 4, 8, 16, 64, 256];

    public string PlayPauseGlyph => IsSimulating ? "❚❚" : "▶";

    /// <summary>
    /// Segment index the simulation has reached, or -1 when the bar is closed.
    ///
    /// Keyed off the bar being open rather than off the position, so a paused
    /// simulation parked at the very start still shows the head at the start
    /// instead of showing nothing.
    /// </summary>
    public int ProgressSegment => SimulationVisible
        ? _simulator?.AtFraction(SimulationFraction).SegmentIndex ?? -1
        : -1;

    /// <summary>Where to draw the head: the simulated position when replaying, the machine otherwise.</summary>
    public Avalonia.Point? DisplayHeadPosition
    {
        get
        {
            if (_simulator is not null && SimulationVisible)
            {
                var state = _simulator.AtFraction(SimulationFraction);
                return new Avalonia.Point(state.Position.X, state.Position.Y);
            }
            return HeadPosition;
        }
    }

    private void RebuildSimulator()
    {
        StopSimulation();

        if (_cam is null)
        {
            _simulator = null;
            SimulationStatus = "No job";
            return;
        }

        var limits = _device?.Settings.Count > 0
            ? MachineLimits.FromSettings(_device.Settings)
            : new MachineLimits
            {
                MaxRateX = SelectedMachine.MaxSpeedMmMin,
                MaxRateY = SelectedMachine.MaxSpeedMmMin,
                AccelerationX = SelectedMachine.AccelerationX,
                AccelerationY = SelectedMachine.AccelerationY,
                JunctionDeviation = SelectedMachine.JunctionDeviation,
            };

        _simulator = new ToolpathSimulator(_cam.Toolpath, limits);
        SimulationStatus = $"Ready · {TimeEstimator.Format(_simulator.Total)}";

        OnPropertyChanged(nameof(GcodeText));
        OnPropertyChanged(nameof(GcodeSummary));
    }

    [RelayCommand]
    private void PlaySimulation()
    {
        if (_simulator is null) RebuildSimulator();
        if (_simulator is null || _simulator.Total <= TimeSpan.Zero) return;

        if (IsSimulating)
        {
            PauseSimulation();
            return;
        }

        // Restarting from the end should begin again rather than sit still.
        if (SimulationFraction >= 0.999) SimulationFraction = 0;

        _playbackOffset = TimeSpan.FromSeconds(SimulationFraction * _simulator.Total.TotalSeconds);
        _playbackStarted = DateTimeOffset.UtcNow;
        IsSimulating = true;
        SimulationVisible = true;

        _playback ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        _playback.Tick -= OnPlaybackTick;
        _playback.Tick += OnPlaybackTick;
        _playback.Start();
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        if (_simulator is null || _simulator.Total <= TimeSpan.Zero)
        {
            StopSimulation();
            return;
        }

        var wall = DateTimeOffset.UtcNow - _playbackStarted;
        var simulated = _playbackOffset + wall * Math.Max(1, SimulationSpeed);

        if (simulated >= _simulator.Total)
        {
            SimulationFraction = 1;
            PauseSimulation();
            SimulationStatus = $"Finished · {TimeEstimator.Format(_simulator.Total)}";
            return;
        }

        SimulationFraction = simulated.TotalSeconds / _simulator.Total.TotalSeconds;
    }

    [RelayCommand]
    private void PauseSimulation()
    {
        _playback?.Stop();
        IsSimulating = false;
        RaiseSimulationState();
    }

    [RelayCommand]
    private void StopSimulation()
    {
        _playback?.Stop();
        IsSimulating = false;
        SimulationVisible = false;
        SimulationFraction = 0;
        _playbackOffset = TimeSpan.Zero;
        RaiseSimulationState();
    }

    partial void OnSimulationFractionChanged(double value)
    {
        if (_simulator is null) return;

        var state = _simulator.AtFraction(value);
        SimulationStatus =
            $"{ToolpathSimulator.Describe(state)} · {TimeEstimator.Format(state.Elapsed)} " +
            $"of {TimeEstimator.Format(_simulator.Total)} · " +
            $"{Core.Units.UnitConvert.FromMm(state.Position.X, DisplayUnit):0.#}, " +
            $"{Core.Units.UnitConvert.FromMm(state.Position.Y, DisplayUnit):0.#} {DisplayUnit.Suffix()}";

        RaiseSimulationState();
    }

    partial void OnIsSimulatingChanged(bool value) => RaiseSimulationState();

    partial void OnSimulationVisibleChanged(bool value) => RaiseSimulationState();

    private void RaiseSimulationState()
    {
        OnPropertyChanged(nameof(ProgressSegment));
        OnPropertyChanged(nameof(DisplayHeadPosition));
        OnPropertyChanged(nameof(PlayPauseGlyph));
    }

    // -------------------------------------------------------------- G-code

    /// <summary>
    /// The exact lines that will be sent. Capped for display — a raster job runs to
    /// hundreds of thousands of lines and no text control survives that, nor would
    /// anybody read it.
    /// </summary>
    public string GcodeText
    {
        get
        {
            if (_cam is null) return "No job generated yet.";

            const int limit = 3000;
            var lines = _cam.Job.Lines;
            var sb = new StringBuilder();

            var shown = Math.Min(limit, lines.Count);
            for (var i = 0; i < shown; i++)
            {
                sb.Append((i + 1).ToString().PadLeft(6)).Append("  ").AppendLine(lines[i]);
            }

            if (lines.Count > limit)
            {
                sb.AppendLine();
                sb.AppendLine($"… {lines.Count - limit:N0} more lines not shown. Use Export G-code to see all of it.");
            }

            return sb.ToString();
        }
    }

    public string GcodeSummary => _cam is null
        ? "—"
        : $"{_cam.Job.LineCount:N0} lines · {_cam.Toolpath.Count:N0} moves · " +
          $"{_cam.Toolpath.CutLengthMm / 1000:0.##} m cutting, {_cam.Toolpath.TravelLengthMm / 1000:0.##} m travel";

    [RelayCommand]
    private void ToggleGcode()
    {
        ShowGcode = !ShowGcode;
        if (ShowGcode)
        {
            OnPropertyChanged(nameof(GcodeText));
            OnPropertyChanged(nameof(GcodeSummary));
        }
    }

    [RelayCommand]
    private async Task CopyGcodeAsync()
    {
        if (_cam is null || TopLevel?.Clipboard is not { } clipboard) return;

        await clipboard.SetTextAsync(string.Join('\n', _cam.Job.Lines)).ConfigureAwait(true);
        Console.AppendInfo($"Copied {_cam.Job.LineCount:N0} lines of G-code to the clipboard.");
    }
}
