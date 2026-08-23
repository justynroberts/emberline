using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using OpenBurn.Cam;
using OpenBurn.Core.Jobs;
using OpenBurn.Core.Machines;
using OpenBurn.Devices;
using OpenBurn.GCode;
using OpenBurn.Transport;
using OpenBurn.Transport.Discovery;

namespace OpenBurn.App.ViewModels;

/// <summary>Connection, machine control and job execution.</summary>
public sealed partial class MainViewModel
{
    // ------------------------------------------------------------ discovery

    [RelayCommand]
    public void RefreshPorts()
    {
        SerialPorts.Clear();
        foreach (var port in SerialPortEnumerator.List()) SerialPorts.Add(port);

        SelectedPort ??= SerialPorts.FirstOrDefault(p => p.LikelyController)?.PortName
                         ?? SerialPorts.FirstOrDefault()?.PortName;
    }

    /// <summary>
    /// Sweep the local network. Opt-in and clearly labelled, because scanning
    /// somebody's LAN unprompted is rude and, on a corporate network, alarming.
    /// </summary>
    [RelayCommand]
    private async Task ScanNetworkAsync()
    {
        IsBusy = true;
        BusyMessage = "Scanning the local network for machines…";
        DiscoveryProgress = 0;
        DiscoveredDevices.Clear();

        try
        {
            var progress = new Progress<double>(p => Dispatcher.UIThread.Post(() => DiscoveryProgress = p));
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            var found = await _discovery.ScanSubnetAsync(progress, cancellationToken: cts.Token).ConfigureAwait(true);
            foreach (var device in found) DiscoveredDevices.Add(device);

            Console.AppendInfo(found.Count == 0
                ? "No network machines answered. Check the laser is on the same network, or connect over USB."
                : $"Found {found.Count} machine(s) on the network.");
        }
        catch (Exception ex)
        {
            Console.AppendError($"Network scan failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            DiscoveryProgress = 0;
        }
    }

    [RelayCommand]
    private async Task ConnectDiscoveredAsync(DiscoveredDevice? device)
    {
        if (device is null) return;
        NetworkAddress = device.Address;
        await ConnectAsync(device.Transport).ConfigureAwait(true);
    }

    // ----------------------------------------------------------- connection

    [RelayCommand]
    private Task ConnectSerialAsync() => ConnectAsync(ConnectionKind.Serial);

    [RelayCommand]
    private Task ConnectNetworkAsync() => ConnectAsync(ConnectionKind.Tcp);

    [RelayCommand]
    private Task ConnectVirtualAsync() => ConnectAsync(ConnectionKind.Virtual);

    private Task ConnectAsync(TransportKind kind) => ConnectAsync(kind switch
    {
        TransportKind.Serial => ConnectionKind.Serial,
        TransportKind.WebSocket => ConnectionKind.WebSocket,
        TransportKind.Http => ConnectionKind.Http,
        TransportKind.Virtual => ConnectionKind.Virtual,
        _ => ConnectionKind.Tcp,
    });

    public async Task ConnectAsync(ConnectionKind kind)
    {
        await DisconnectAsync().ConfigureAwait(true);

        IsBusy = true;
        BusyMessage = "Connecting…";

        try
        {
            var profile = SelectedMachine;
            if (kind == ConnectionKind.Virtual && profile.DriverId != "grbl")
            {
                // The simulator speaks plain GRBL, so use the plain driver for it.
                profile = profile with { DriverId = "grbl" };
            }

            var address = kind switch
            {
                ConnectionKind.Serial => SelectedPort,
                ConnectionKind.Virtual => null,
                // What is in the box, and nothing else. Falling back to a
                // remembered address would connect to a machine the panel is not
                // showing.
                _ => NetworkAddress,
            };

            if (kind == ConnectionKind.Serial && string.IsNullOrWhiteSpace(address))
            {
                Console.AppendError("Choose a serial port first.");
                return;
            }
            if (kind is ConnectionKind.Tcp or ConnectionKind.WebSocket or ConnectionKind.Http &&
                string.IsNullOrWhiteSpace(address))
            {
                Console.AppendError("Enter the machine's address, or run a network scan.");
                return;
            }

            var transport = DeviceFactory.CreateTransport(profile, kind, address);
            var device = DeviceFactory.CreateDevice(profile);
            AttachDevice(device);

            await device.ConnectAsync(transport).ConfigureAwait(true);

            _device = device;
            RaiseConnectionState();
            QueueRegenerate();
        }
        catch (Exception ex)
        {
            Console.AppendError($"Could not connect: {ex.Message}");
            _device = null;
            RaiseConnectionState();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AttachDevice(ILaserDevice device)
    {
        device.ConsoleOutput += Console.Append;

        device.ConnectionChanged += _ => Dispatcher.UIThread.Post(RaiseConnectionState);

        device.StatusChanged += _ => Dispatcher.UIThread.Post(RaiseReadouts);

        device.ProgressChanged += progress => Dispatcher.UIThread.Post(() =>
        {
            RecordJobProgress(progress);
            OnPropertyChanged(nameof(JobProgress));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(JobState));
            OnPropertyChanged(nameof(IsJobRunning));
            FollowJobContext(IsJobRunning);
            OnPropertyChanged(nameof(IsJobPaused));
            OnPropertyChanged(nameof(IsJobActive));
            OnPropertyChanged(nameof(CanStartJob));
            OnPropertyChanged(nameof(ShowLiveView));
            StartJobCommand.NotifyCanExecuteChanged();
        });

        device.Fault += (info, isAlarm) => Dispatcher.UIThread.Post(() =>
        {
            LastFault = info;
            LastFaultIsAlarm = isAlarm;
            OnPropertyChanged(nameof(LastFault));
            OnPropertyChanged(nameof(HasFault));
            OnPropertyChanged(nameof(FaultText));
        });
    }

    private Core.Storage.JobLibrary? _library;
    private Core.Jobs.JobRecord? _currentRecord;

    private Core.Storage.JobLibrary Library => _library ??= new Core.Storage.JobLibrary();

    /// <summary>
    /// Keep the job library up to date as a job runs.
    ///
    /// The record is written when the job starts, not when it finishes, so a job
    /// that fails or that the user stops still leaves a trace — which is exactly
    /// the case somebody wants to look up afterwards.
    /// </summary>
    private void RecordJobProgress(Core.Jobs.JobProgress progress)
    {
        try
        {
            if (progress.State == Core.Jobs.JobState.Running && _currentRecord is null)
            {
                var layer = Layers.FirstOrDefault(l => l.Enabled);
                var bounds = _cam?.Job.Bounds ?? Core.Geometry.Rect2.Empty;

                _currentRecord = new Core.Jobs.JobRecord
                {
                    Name = Design.Name,
                    StartedAt = DateTimeOffset.UtcNow,
                    Outcome = Core.Jobs.JobState.Running,
                    MachineName = SelectedMachine.DisplayName,
                    MaterialName = SelectedMaterial?.DisplayName,
                    SpeedMmMin = layer?.SpeedMmMin ?? 0,
                    PowerPercent = layer?.PowerPercent ?? 0,
                    Passes = layer?.Passes ?? 1,
                    TotalLines = progress.TotalLines,
                    LinesCompleted = 0,
                    WidthMm = bounds.IsEmpty ? 0 : bounds.Width,
                    HeightMm = bounds.IsEmpty ? 0 : bounds.Height,
                    SourceFiles = _cam?.Job.SourceFiles ?? [],
                    GcodePath = Settings.ArchiveJobGcode && _cam is not null
                        ? Core.Storage.JobLibrary.ArchiveGcode(Guid.NewGuid().ToString("N"), _cam.Job.Lines)
                        : null,
                };

                Library.Record(_currentRecord);
                return;
            }

            if (_currentRecord is null || !progress.State.IsTerminal()) return;

            var finished = _currentRecord with
            {
                FinishedAt = DateTimeOffset.UtcNow,
                Outcome = progress.State,
                LinesCompleted = progress.LinesAcknowledged,
                FailureReason = progress.State == Core.Jobs.JobState.Failed ? LastFault?.Title : null,
            };

            Library.Record(finished);
            _currentRecord = null;

            Console.AppendInfo($"Job recorded: {finished.Summary} — {progress.State}.");
        }
        catch (Exception ex)
        {
            // The library is a convenience; never let it interfere with a running job.
            Console.AppendError($"Could not update the job library: {ex.Message}");
            _currentRecord = null;
        }
    }

    /// <summary>The job library, for the history window.</summary>
    public Core.Storage.JobLibrary JobLibrary => Library;

    /// <summary>A settings editor bound to the connected machine.</summary>
    public ControllerSettingsViewModel CreateSettingsEditor() =>
        new(_device, text => Console.AppendInfo(text));

    /// <summary>Recent jobs, newest first.</summary>
    public IReadOnlyList<Core.Jobs.JobRecord> RecentJobs
    {
        get
        {
            try
            {
                return Library.Recent(25);
            }
            catch (Exception)
            {
                return [];
            }
        }
    }

    public GCode.Grbl.GrblCodeInfo? LastFault { get; private set; }
    public bool LastFaultIsAlarm { get; private set; }
    public bool HasFault => LastFault is not null;

    public string FaultText => LastFault is null
        ? string.Empty
        : $"{(LastFaultIsAlarm ? "Alarm" : "Error")} {LastFault.Code}: {LastFault.Title}. {LastFault.Remedy}";

    [RelayCommand]
    private void DismissFault()
    {
        LastFault = null;
        OnPropertyChanged(nameof(HasFault));
        OnPropertyChanged(nameof(FaultText));
    }

    private void RaiseConnectionState()
    {
        OnPropertyChanged(nameof(Connection));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanStartJob));
        OnPropertyChanged(nameof(IsHomed));
        RaiseReadouts();
        StartJobCommand.NotifyCanExecuteChanged();
        FrameCommand.NotifyCanExecuteChanged();

        StatusText = Connection switch
        {
            ConnectionState.Connected => $"{SelectedMachine.DisplayName} connected",
            ConnectionState.Connecting => "Connecting…",
            ConnectionState.Reconnecting => "Reconnecting…",
            ConnectionState.Faulted => "Link lost",
            _ => "Disconnected",
        };
    }

    [RelayCommand]
    public async Task DisconnectAsync()
    {
        if (_device is null) return;
        try
        {
            await _device.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Console.AppendError($"Error while disconnecting: {ex.Message}");
        }
        _device = null;
        RaiseConnectionState();
    }

    // -------------------------------------------------------- machine control

    private async Task GuardAsync(Func<ILaserDevice, Task> action, string what)
    {
        if (_device is null)
        {
            Console.AppendError("Not connected.");
            return;
        }
        try
        {
            await action(_device).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Console.AppendError($"{what} failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private Task HomeAsync() => GuardAsync(d => d.HomeAsync(), "Homing");

    [RelayCommand]
    private Task UnlockAsync() => GuardAsync(d => d.UnlockAsync(), "Unlock");

    [RelayCommand]
    private Task SoftResetAsync() => GuardAsync(d => d.SoftResetAsync(), "Soft reset");

    /// <summary>
    /// Feed hold, spindle stop, then soft reset. Bound to Escape as well as the
    /// button, because in an emergency nobody hunts for a control with a mouse.
    /// </summary>
    [RelayCommand]
    private Task EmergencyStopAsync() => GuardAsync(d => d.EmergencyStopAsync(), "Emergency stop");

    [RelayCommand]
    private Task ZeroAllAsync() => GuardAsync(d => d.SetWorkZeroAsync(), "Set work zero");

    [RelayCommand]
    private Task ZeroXAsync() => GuardAsync(d => d.SetWorkZeroAsync(x: true, y: false, z: false), "Set X zero");

    [RelayCommand]
    private Task ZeroYAsync() => GuardAsync(d => d.SetWorkZeroAsync(x: false, y: true, z: false), "Set Y zero");

    [RelayCommand]
    private Task JogAsync(string? direction) => GuardAsync(d =>
    {
        var step = JogStepMm;
        var (dx, dy) = direction switch
        {
            "left" => (-step, 0.0),
            "right" => (step, 0.0),
            "up" => (0.0, step),
            "down" => (0.0, -step),
            "upleft" => (-step, step),
            "upright" => (step, step),
            "downleft" => (-step, -step),
            "downright" => (step, -step),
            _ => (0.0, 0.0),
        };
        return dx == 0 && dy == 0 ? Task.CompletedTask : d.JogAsync(dx, dy, 0, JogFeedMmMin);
    }, "Jog");

    [RelayCommand]
    private Task CancelJogAsync() => GuardAsync(d => d.CancelJogAsync(), "Cancel jog");

    [RelayCommand]
    private Task GoToOriginAsync() => GuardAsync(d => d.MoveToAsync(0, 0, JogFeedMmMin), "Move to origin");

    public Task MoveHeadToAsync(double xMm, double yMm) =>
        GuardAsync(d => d.MoveToAsync(xMm, yMm, JogFeedMmMin), "Move");

    [RelayCommand]
    private Task SetFeedOverrideAsync(string? percent) =>
        GuardAsync(d => d.SetFeedOverrideAsync(int.TryParse(percent, out var p) ? p : 100), "Feed override");

    [RelayCommand]
    private Task SetPowerOverrideAsync(string? percent) =>
        GuardAsync(d => d.SetSpindleOverrideAsync(int.TryParse(percent, out var p) ? p : 100), "Power override");

    private Task SendConsoleCommandAsync(string line) => GuardAsync(d => d.SendRawAsync(line), "Command");

    [RelayCommand]
    private async Task ReadSettingsAsync()
    {
        await GuardAsync(async d =>
        {
            var settings = await d.ReadSettingsAsync().ConfigureAwait(true);
            Console.AppendInfo($"Read {settings.Count} controller settings.");

            foreach (var warning in GCode.Grbl.GrblSettings.Audit(settings))
            {
                if (warning.IsError) Console.AppendError(warning.Text);
                else Console.Append(new ConsoleEntry(DateTimeOffset.UtcNow, ConsoleDirection.Warning, warning.Text));
            }
            QueueRegenerate();
        }, "Reading settings").ConfigureAwait(true);
    }

    // ------------------------------------------------------------------- job

    private bool CanRunJob() => CanStartJob;

    [RelayCommand(CanExecute = nameof(CanRunJob))]
    private async Task StartJobAsync()
    {
        // Flush any pending regeneration first: an edit made a moment ago may still
        // be sitting in the debounce timer, and running the previous toolpath is an
        // expensive way to discover that.
        RegenerateNow();

        if (_device is null || _cam is null) return;

        if (HasBlockingIssue)
        {
            Console.AppendError("This job cannot run yet: " + IssueSummary);
            return;
        }

        if (Settings.WarnWhenNotHomed && !_device.IsHomed &&
            SelectedMachine.Capabilities.HasFlag(MachineCapabilities.Homing))
        {
            Console.Append(new ConsoleEntry(DateTimeOffset.UtcNow, ConsoleDirection.Warning,
                "The machine has not been homed this session. Position is whatever it happens to be — home first if you want repeatability."));
        }

        await GuardAsync(d => d.StartJobAsync(_cam.Job), "Starting the job").ConfigureAwait(true);
    }

    [RelayCommand]
    private Task PauseJobAsync() => GuardAsync(d => d.PauseJobAsync(), "Pause");

    [RelayCommand]
    private Task ResumeJobAsync() => GuardAsync(d => d.ResumeJobAsync(), "Resume");

    [RelayCommand]
    private Task StopJobAsync() => GuardAsync(d => d.StopJobAsync(), "Stop");

    private bool CanFrame() => IsConnected && !IsJobActive && _cam is not null;

    /// <summary>
    /// Trace the outline at pointer power. The last chance to notice the artwork is
    /// twenty millimetres off before the beam marks the workpiece.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFrame))]
    private async Task FrameAsync()
    {
        RegenerateNow();
        if (_cam is null) return;

        var outlines = Design.AllOutlines();
        if (outlines.Count == 0)
        {
            Console.AppendError("There is nothing to frame.");
            return;
        }

        var options = new FramingOptions
        {
            Mode = FramingMode.Rectangle,
            FeedRate = Math.Min(3000, SelectedMachine.TravelSpeedMmMin),
            PowerPercent = 0.4,
            Repeats = 2,
        };

        await GuardAsync(d => d.FrameAsync(outlines, options), "Framing").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RunTestGridAsync()
    {
        var grid = CamPipeline.GenerateTestGrid(
            SelectedMachine,
            [20, 40, 60, 80, 100],
            [500, 1000, 2000, 3000, 6000],
            cellSizeMm: 8);

        Console.AppendInfo($"Test grid: 5 powers × 5 speeds, {TimeEstimator.Format(grid.Estimate.Total)} estimated. " +
                           "Rows are power, columns are speed. Burn it on an offcut of the material you are about to use.");

        if (!grid.CanRun)
        {
            Console.AppendError("The test grid does not fit the bed: " +
                                string.Join("; ", grid.Issues.Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.Detail)));
            return;
        }

        await GuardAsync(d => d.StartJobAsync(grid.Job), "Starting the test grid").ConfigureAwait(true);
    }
}
