using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emberline.Cam;
using Emberline.Cam.Import;
using Emberline.Cam.Trace;
using Emberline.Core.Documents;
using Emberline.Core.Geometry;
using Emberline.Core.Jobs;
using Emberline.Core.Machines;
using Emberline.Core.Storage;
using Emberline.Core.Units;
using Emberline.Devices;
using Emberline.GCode;
using Emberline.GCode.Grbl;
using Emberline.Materials;
using Emberline.Transport;
using Emberline.Transport.Discovery;

namespace Emberline.App.ViewModels;

/// <summary>
/// The application shell.
///
/// Owns the document, the machine, and the connection between them. Everything the
/// UI can do arrives here as a command, which keeps the safety rules in one place:
/// nothing that moves the gantry or fires the beam happens without an explicit
/// user action, and the two that can ruin a workpiece — Start and Frame — check
/// the validator first.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private DispatcherTimer? _regenerateTimer;
    private readonly DeviceDiscovery _discovery = new();
    private ILaserDevice? _device;
    private CamResult? _cam;
    private bool _regeneratePending;

    public MainViewModel(AppSettings settings)
    {
        Settings = settings;
        Machines = MachineLibrary.Load();
        MaterialLibrary = MaterialLibrary.CreateDefault();
        _ = MaterialLibrary.LoadAsync(AppPaths.MaterialsFile);

        Design = Core.Documents.Design.CreateDefault();
        Undo.Changed += RaiseUndoState;
        SelectedMachine = Machines.Find(settings.LastMachineId ?? string.Empty) ?? Machines.Default;

        // Show the address that a Wi-Fi press will actually use. It used to be
        // remembered in settings but left out of the box, so the panel showed an
        // empty field with placeholder text while Connect quietly fell back to
        // the last machine — the operator could not see what they were about to
        // open a connection to, and a stray press reached real hardware with
        // nothing on screen to say which.
        NetworkAddress = settings.LastNetworkAddress ?? string.Empty;

        DisplayUnit = settings.DisplayUnit;
        Theme = settings.Theme;
        ShowGrid = settings.ShowGridLines;
        ShowTravel = settings.ShowTravelMoves;
        JogStepMm = settings.JogStepMm;
        JogFeedMmMin = settings.JogFeedMmMin;

        Console.Submit = SendConsoleCommandAsync;

        RebuildLayers();

        // Regeneration is debounced: dragging a power slider must not re-run the
        // whole CAM pipeline on every pixel of movement.
        _regenerateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _regenerateTimer.Tick += (_, _) =>
        {
            _regenerateTimer.Stop();
            if (_regeneratePending) RegenerateNow();
        };

        RefreshPorts();
        RebuildWorkpiecePresets();
        LoadPlugins();
        QueueRegenerate();

        // Setting up is not editing. An empty document that claims unsaved changes
        // teaches people to ignore the mark, which is the one thing it must not do.
        HasUnsavedChanges = false;
        Console.AppendInfo("Emberline ready. Select a machine and connect, or pick the virtual laser to try it without hardware.");
    }

    // ------------------------------------------------------------------ state

    public AppSettings Settings { get; private set; }
    public MachineLibrary Machines { get; }
    public MaterialLibrary MaterialLibrary { get; }
    public ConsoleViewModel Console { get; } = new();

    public ObservableCollection<LayerViewModel> Layers { get; } = [];
    public ObservableCollection<SerialPortInfo> SerialPorts { get; } = [];
    public ObservableCollection<DiscoveredDevice> DiscoveredDevices { get; } = [];
    public ObservableCollection<ValidationIssue> Issues { get; } = [];

    [ObservableProperty]
    private Design _design;

    [ObservableProperty]
    private MachineProfile _selectedMachine;

    [ObservableProperty]
    private LayerViewModel? _selectedLayer;

    [ObservableProperty]
    private Toolpath? _toolpath;

    [ObservableProperty]
    private LengthUnit _displayUnit;

    [ObservableProperty]
    private ThemeMode _theme;

    [ObservableProperty]
    private bool _showGrid;

    [ObservableProperty]
    private bool _showTravel;

    [ObservableProperty]
    private bool _showConsole;

    [ObservableProperty]
    private double _jogStepMm;

    [ObservableProperty]
    private double _jogFeedMmMin;

    [ObservableProperty]
    private string _statusText = "Disconnected";

    [ObservableProperty]
    private string _cursorText = "—";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyMessage = string.Empty;

    [ObservableProperty]
    private double _discoveryProgress;

    [ObservableProperty]
    private string? _selectedPort;

    [ObservableProperty]
    private string _networkAddress = string.Empty;

    // -------------------------------------------------------- derived display

    public ConnectionState Connection => _device?.Connection ?? ConnectionState.Disconnected;
    public bool IsConnected => Connection == ConnectionState.Connected;
    public GrblStatus MachineStatus => _device?.Status ?? GrblStatus.Disconnected;
    public JobState JobState => _device?.JobState ?? JobState.Idle;
    public JobProgress JobProgress => _device?.Progress ?? JobProgress.Empty;
    public bool IsHomed => _device?.IsHomed ?? false;

    /// <summary>How many controller settings have been read. Zero means none yet.</summary>
    public int MachineSettingCount => _device?.Settings.Count ?? 0;

    public bool IsJobRunning => JobState is JobState.Running;
    public bool IsJobPaused => JobState is JobState.Paused;
    public bool IsJobActive => JobState.IsActive();
    public bool CanStartJob => IsConnected && !IsJobActive && _cam is { CanRun: true };

    /// <summary>
    /// Why Start is greyed out, in words, shown as its tooltip.
    ///
    /// A disabled button with no explanation is the single most confusing thing
    /// in a machine controller for somebody who has not used one: nothing on
    /// screen says whether the problem is the machine, the artwork or the
    /// settings. The button knows; it should say.
    /// </summary>
    public string StartHint
    {
        get
        {
            if (IsJobRunning) return "A job is already running. Pause or stop it first.";
            if (IsJobPaused) return "The job is paused. Use Resume to carry on from where it stopped.";

            if (!IsConnected)
            {
                return "Not connected to a machine yet. Press USB or Wi-Fi in the machine panel — " +
                       "or Virtual, which runs the whole job against a built-in simulator so you can " +
                       "try everything without hardware.";
            }

            if (Design.Shapes.Count == 0)
            {
                return "There is nothing to burn yet. Open a file, or draw a shape or add text " +
                       "with the tools down the left-hand side.";
            }

            if (_cam is { CanRun: false })
            {
                return IssueSummary is { Length: > 0 } why
                    ? "This job cannot run yet: " + why
                    : "This job cannot run yet — see the Job panel for what is blocking it.";
            }

            return "Send the job to the machine and start burning. Press Frame first if you have not " +
                   "checked where it will land on the material.";
        }
    }

    public string MachineStateName => IsConnected ? MachineStatus.State.ToString() : "Disconnected";

    public string StateColourKey => !IsConnected ? "StateOff" : MachineStatus.State switch
    {
        MachineState.Run => "StateRun",
        MachineState.Hold or MachineState.Door => "StateHold",
        MachineState.Alarm => "StateAlarm",
        MachineState.Jog or MachineState.Home => "StateJog",
        _ => "StateIdle",
    };

    public string WorkPositionText =>
        $"X {UnitConvert.FromMm(MachineStatus.WorkPosition.X, DisplayUnit):0.00}   " +
        $"Y {UnitConvert.FromMm(MachineStatus.WorkPosition.Y, DisplayUnit):0.00}";

    public string MachinePositionText =>
        $"X {UnitConvert.FromMm(MachineStatus.MachinePosition.X, DisplayUnit):0.00}   " +
        $"Y {UnitConvert.FromMm(MachineStatus.MachinePosition.Y, DisplayUnit):0.00}";

    public string FeedPowerText => $"F {MachineStatus.Feed:0}   S {MachineStatus.Spindle:0}";

    public string BufferText => MachineStatus.Buffer is { } b ? $"{b.PlannerBlocks}/{b.RxBytes}" : "—";

    public Avalonia.Point? HeadPosition => IsConnected
        ? new Avalonia.Point(MachineStatus.WorkPosition.X, MachineStatus.WorkPosition.Y)
        : null;

    public string EstimateText => _cam is null ? "—" : TimeEstimator.Format(_cam.Estimate.Total);

    public string JobSizeText
    {
        get
        {
            var b = _cam?.Job.Bounds ?? Rect2.Empty;
            if (b.IsEmpty) return "—";
            return $"{UnitConvert.FromMm(b.Width, DisplayUnit):0.#} × {UnitConvert.FromMm(b.Height, DisplayUnit):0.#} {DisplayUnit.Suffix()}";
        }
    }

    public string LineCountText => _cam is null ? "—" : $"{_cam.Job.LineCount:N0} lines";

    public string ProgressText
    {
        get
        {
            var p = JobProgress;
            if (p.TotalLines == 0) return "Ready";
            return $"{p.Percent:0.#}%  ·  {p.LinesAcknowledged:N0}/{p.TotalLines:N0}  ·  " +
                   $"{TimeEstimator.Format(p.Elapsed)} elapsed  ·  {TimeEstimator.Format(p.EstimatedRemaining)} left";
        }
    }

    public bool HasBlockingIssue => Issues.Any(i => i.Severity == ValidationSeverity.Error);

    public string? IssueSummary
    {
        get
        {
            var error = Issues.FirstOrDefault(i => i.Severity == ValidationSeverity.Error);
            if (error is not null) return $"{error.Title} — {error.Detail}";
            var warning = Issues.FirstOrDefault(i => i.Severity == ValidationSeverity.Warning);
            return warning is null ? null : $"{warning.Title} — {warning.Detail}";
        }
    }

    public static string Version =>
        typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    // ---------------------------------------------------------------- machine

    partial void OnSelectedMachineChanged(MachineProfile value)
    {
        Settings = Settings with { LastMachineId = value.Id };
        OnPropertyChanged(nameof(BedWidthMm));
        OnPropertyChanged(nameof(BedHeightMm));
        OnPropertyChanged(nameof(MachineHasRotary));
        QueueRegenerate();
    }

    public double BedWidthMm => SelectedMachine.BedWidthMm;
    public double BedHeightMm => SelectedMachine.BedHeightMm;

    partial void OnThemeChanged(ThemeMode value)
    {
        App.ApplyTheme(value);
        Settings = Settings with { Theme = value };
    }

    partial void OnDisplayUnitChanged(LengthUnit value)
    {
        Settings = Settings with { DisplayUnit = value };
        RaiseReadouts();
    }

    partial void OnShowGridChanged(bool value) => Settings = Settings with { ShowGridLines = value };
    partial void OnShowTravelChanged(bool value) => Settings = Settings with { ShowTravelMoves = value };

    // ------------------------------------------------------------------- CAM

    /// <summary>
    /// Ask for a fresh toolpath soon. Null-tolerant on the timer because property
    /// setters fire during construction, before the timer exists — an ordering trap
    /// that otherwise kills the application on startup with a null reference.
    /// </summary>
    /// <summary>
    /// Bumped on every change to the document. The canvas caches its geometry
    /// against this rather than rebuilding it each frame.
    /// </summary>
    [ObservableProperty]
    private int _documentVersion;

    public void QueueRegenerate()
    {
        DocumentVersion++;
        HasUnsavedChanges = true;
        _regeneratePending = true;
        if (_regenerateTimer is null) return;
        _regenerateTimer.Stop();
        _regenerateTimer.Start();
    }

    /// <summary>
    /// Regenerate immediately, bypassing the debounce.
    ///
    /// Anything that is about to act on the job — starting it, framing it, saving
    /// it — must call this first. Otherwise a change made in the last fifth of a
    /// second is still sitting in the timer and the machine runs the previous
    /// toolpath, which is a very expensive way to find out about a race.
    /// </summary>
    public void RegenerateNow()
    {
        _regeneratePending = false;
        _regenerateTimer?.Stop();
        try
        {
            var settings = _device?.Settings.Count > 0 ? _device.Settings : null;
            _cam = CamPipeline.Generate(
                Design,
                SelectedMachine,
                CamOptions.Default with { Rotary = Rotary, DitherOptions = DitherOptions },
                settings,
                IsHomed);
            Toolpath = _cam.Toolpath;

            Issues.Clear();
            foreach (var issue in _cam.Issues) Issues.Add(issue);
        }
        catch (Exception ex)
        {
            Console.AppendError($"Could not generate the job: {ex.Message}");
            _cam = null;
            Toolpath = null;
        }

        RebuildSimulator();
        RaiseJobReadouts();
    }

    private void RaiseJobReadouts()
    {
        OnPropertyChanged(nameof(EstimateText));
        OnPropertyChanged(nameof(JobSizeText));
        OnPropertyChanged(nameof(LineCountText));
        OnPropertyChanged(nameof(CanStartJob));
        OnPropertyChanged(nameof(StartHint));
        OnPropertyChanged(nameof(HasBlockingIssue));
        OnPropertyChanged(nameof(IssueSummary));
        StartJobCommand.NotifyCanExecuteChanged();
        FrameCommand.NotifyCanExecuteChanged();
    }

    private void RaiseReadouts()
    {
        OnPropertyChanged(nameof(WorkPositionText));
        OnPropertyChanged(nameof(MachinePositionText));
        OnPropertyChanged(nameof(FeedPowerText));
        OnPropertyChanged(nameof(BufferText));
        OnPropertyChanged(nameof(MachineStateName));
        OnPropertyChanged(nameof(StateColourKey));
        OnPropertyChanged(nameof(HeadPosition));

        // The canvas binds DisplayHeadPosition, not HeadPosition — it has to,
        // because during a replay the crosshair follows the simulation instead of
        // the machine. Raising only the underlying one leaves the coordinate
        // readout ticking along while the crosshair sits still, which looks like
        // the machine is not moving.
        OnPropertyChanged(nameof(DisplayHeadPosition));

        OnPropertyChanged(nameof(JobSizeText));
    }

    private void RebuildLayers()
    {
        Layers.Clear();
        foreach (var layer in Design.Layers.OrderBy(l => l.Order))
        {
            Layers.Add(new LayerViewModel(layer, QueueRegenerate));
        }
        SelectedLayer = Layers.FirstOrDefault();
    }

    /// <summary>
    /// Re-read the machine list after the profile editor has been used, keeping
    /// the selection on whatever was being edited.
    /// </summary>
    public void ReloadMachines(string? selectId)
    {
        OnPropertyChanged(nameof(Machines));

        var wanted = selectId ?? SelectedMachine.Id;
        if (Machines.Find(wanted) is { } found) SelectedMachine = found;

        OnPropertyChanged(nameof(BedWidthMm));
        OnPropertyChanged(nameof(BedHeightMm));
        OnPropertyChanged(nameof(MachineHasRotary));
        QueueRegenerate();
    }

    public void SetCursor(Vec2 mm) =>
        CursorText = $"{UnitConvert.FromMm(mm.X, DisplayUnit):0.0}, {UnitConvert.FromMm(mm.Y, DisplayUnit):0.0} {DisplayUnit.Suffix()}";

    public void Dispose()
    {
        // Every timer, not just the regenerate one. A playback timer left running
        // keeps ticking on the dispatcher long after whatever created it has gone.
        _regenerateTimer?.Stop();
        _playback?.Stop();
        _device?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public void PersistOnExit()
    {
        try
        {
            Settings = Settings with
            {
                JogStepMm = JogStepMm,
                JogFeedMmMin = JogFeedMmMin,
                DisplayUnit = DisplayUnit,
                Theme = Theme,
                ShowGridLines = ShowGrid,
                ShowTravelMoves = ShowTravel,
                LastMachineId = SelectedMachine.Id,
                LastSerialPort = SelectedPort,
                LastNetworkAddress = string.IsNullOrWhiteSpace(NetworkAddress) ? Settings.LastNetworkAddress : NetworkAddress,
            };
            Settings.Save();
            MaterialLibrary.SaveAsync(AppPaths.MaterialsFile).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.AppendError($"Could not save settings: {ex.Message}");
        }
    }
}
