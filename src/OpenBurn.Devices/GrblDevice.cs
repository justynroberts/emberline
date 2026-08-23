using System.Globalization;
using System.Text;
using OpenBurn.Core.Geometry;
using OpenBurn.Core.Jobs;
using OpenBurn.Core.Machines;
using OpenBurn.GCode;
using OpenBurn.GCode.Grbl;
using OpenBurn.Transport;

namespace OpenBurn.Devices;

/// <summary>
/// A GRBL 1.1 machine.
///
/// This is where the protocol, the transport and the job engine meet. Three
/// things it deliberately gets right, because each of them is a way real senders
/// go wrong:
///
///  1. Real-time bytes never queue behind G-code. A feed hold issued during a
///     raster must take effect now, not after forty kilobytes have drained.
///  2. Acknowledgements are routed to whoever is waiting. A <c>$$</c> issued
///     mid-job must not consume the job's <c>ok</c>, or the stream desynchronises
///     and silently stalls.
///  3. Losing the link pauses the job rather than losing it, and remembers the
///     line to resume from.
/// </summary>
public sealed class GrblDevice : ILaserDevice
{
    private readonly GrblParser _parser = new();
    private readonly LineAssembler _assembler = new();
    private readonly Dictionary<int, double> _settings = [];
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly Lock _sync = new();

    private ITransport? _transport;
    private GcodeStreamer? _streamer;
    private CancellationTokenSource? _poller;
    private JobDefinition? _job;
    private DateTimeOffset _jobStarted;
    private TimeSpan _jobEstimate;
    private string _currentLine = string.Empty;

    /// <summary>
    /// Out-of-band commands waiting for their own <c>ok</c>. Without this, a console
    /// command sent mid-job steals an acknowledgement from the streamer.
    /// </summary>
    private readonly Queue<TaskCompletionSource<bool>> _pendingCommands = new();

    private TaskCompletionSource<IReadOnlyDictionary<int, double>>? _settingsRead;
    private Dictionary<int, double>? _settingsBuffer;

    public GrblDevice(MachineProfile profile) => Profile = profile;

    public MachineProfile Profile { get; }
    public ConnectionState Connection { get; private set; } = ConnectionState.Disconnected;
    public GrblStatus Status { get; private set; } = GrblStatus.Disconnected;
    public JobState JobState { get; private set; } = JobState.Idle;
    public JobProgress Progress { get; private set; } = JobProgress.Empty;
    public IReadOnlyDictionary<int, double> Settings => _settings;
    public bool IsHomed { get; private set; }

    /// <summary>Firmware banner read at connect, e.g. "1.1h".</summary>
    public string? FirmwareVersion { get; private set; }

    /// <summary>Line to resume from after a link drop or a stop. -1 when there is nothing to resume.</summary>
    public int ResumeLine { get; private set; } = -1;

    /// <summary>Status poll rate. Six hertz keeps the readout live without flooding a slow link.</summary>
    public double StatusPollHz { get; set; } = 6;

    public event Action<ConnectionState>? ConnectionChanged;
    public event Action<GrblStatus>? StatusChanged;
    public event Action<JobProgress>? ProgressChanged;
    public event Action<ConsoleEntry>? ConsoleOutput;
    public event Action<GrblCodeInfo, bool>? Fault;

    // ------------------------------------------------------------ connection

    public async Task ConnectAsync(ITransport transport, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        SetConnection(ConnectionState.Connecting);
        _parser.Reset();
        _assembler.Reset();
        _settings.Clear();
        IsHomed = false;

        _transport = transport;
        transport.DataReceived += OnData;
        transport.Disconnected += OnTransportDropped;

        try
        {
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            transport.DataReceived -= OnData;
            transport.Disconnected -= OnTransportDropped;
            _transport = null;
            SetConnection(ConnectionState.Faulted);
            throw;
        }

        Log(ConsoleDirection.Info, $"Connected via {transport.Description}");
        SetConnection(ConnectionState.Connected);

        StartStatusPolling();

        // Give the controller a moment to finish announcing itself, then apply
        // the profile's init block so units and distance mode are known-good.
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        foreach (var command in Profile.InitCommands)
        {
            await SendRawAsync(command, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log(ConsoleDirection.Warning, "Controller did not return its settings ($$). Time estimates will use profile defaults.");
        }
    }

    public async Task DisconnectAsync()
    {
        StopStatusPolling();

        if (_transport is { } transport)
        {
            transport.DataReceived -= OnData;
            transport.Disconnected -= OnTransportDropped;
            await transport.DisconnectAsync().ConfigureAwait(false);
            _transport = null;
        }

        FailPendingCommands(new IOException("Disconnected."));

        Status = GrblStatus.Disconnected;
        SetConnection(ConnectionState.Disconnected);
    }

    private void OnTransportDropped(Exception? error)
    {
        Log(ConsoleDirection.Error, error is null ? "Link closed." : $"Link lost: {error.Message}");
        StopStatusPolling();
        FailPendingCommands(error ?? new IOException("Link lost."));

        // Losing the link mid-job must not lose the job. Pause and remember the
        // line, so the operator can reconnect and resume rather than start over.
        if (JobState is JobState.Running or JobState.Paused)
        {
            ResumeLine = (_streamer?.CompletedIndex ?? -1) + 1;
            SetJobState(JobState.Paused);
            Log(ConsoleDirection.Warning,
                $"Job paused at line {ResumeLine + 1}. Reconnect and choose Resume to continue from there.");
        }

        Status = GrblStatus.Disconnected;
        SetConnection(ConnectionState.Faulted);
    }

    // --------------------------------------------------------------- receive

    private void OnData(ReadOnlyMemory<byte> data)
    {
        var text = Encoding.ASCII.GetString(data.Span);
        foreach (var line in _assembler.Push(text))
        {
            HandleLine(line);
        }
    }

    private void HandleLine(string rawLine)
    {
        var message = _parser.Parse(rawLine);

        // Status reports arrive several times a second; logging them would drown
        // everything a human wants to read in the console.
        if (message is not GrblMessage.Status) Log(ConsoleDirection.Received, rawLine);

        switch (message)
        {
            case GrblMessage.Ok:
                RouteAcknowledgement(null);
                break;

            case GrblMessage.Error error:
                Fault?.Invoke(error.Info, false);
                Log(ConsoleDirection.Error, $"{error.Info.Title} — {error.Info.Message} {error.Info.Remedy}");
                RouteAcknowledgement(error.Code);
                break;

            case GrblMessage.Alarm alarm:
                IsHomed = false;
                Fault?.Invoke(alarm.Info, true);
                Log(ConsoleDirection.Error, $"ALARM {alarm.Code}: {alarm.Info.Title} — {alarm.Info.Remedy}");
                // An alarm flushes the controller's buffer, so the stream is dead.
                if (JobState is JobState.Running or JobState.Paused)
                {
                    ResumeLine = (_streamer?.CompletedIndex ?? -1) + 1;
                    _streamer?.Abort();
                    SetJobState(JobState.Failed);
                }
                break;

            case GrblMessage.Status status:
                OnStatus(status.Value);
                break;

            case GrblMessage.Welcome welcome:
                FirmwareVersion = welcome.Version;
                IsHomed = false;
                Log(ConsoleDirection.Info, $"GRBL {welcome.Version} ready.");
                break;

            case GrblMessage.Setting setting:
                _settingsBuffer?.TryAdd(setting.Key, setting.Value);
                _settings[setting.Key] = setting.Value;
                break;

            case GrblMessage.Feedback feedback:
                Log(ConsoleDirection.Info, feedback.Text);
                break;

            default:
                break;
        }
    }

    private void OnStatus(GrblStatus status)
    {
        var previous = Status;
        Status = status;

        // Homing completes when the machine leaves Home state without alarming.
        if (previous.State == MachineState.Home && status.State == MachineState.Idle) IsHomed = true;

        StatusChanged?.Invoke(status);

        if (JobState == JobState.Running) RaiseProgress();
    }

    /// <summary>
    /// Decide who an <c>ok</c> or <c>error</c> belongs to.
    ///
    /// Out-of-band commands are answered first because they were issued most
    /// recently and the controller answers in order; only when none are waiting
    /// does the acknowledgement belong to the job stream.
    /// </summary>
    private void RouteAcknowledgement(int? errorCode)
    {
        TaskCompletionSource<bool>? pending = null;
        lock (_sync)
        {
            if (_pendingCommands.Count > 0) pending = _pendingCommands.Dequeue();
        }

        if (pending is not null)
        {
            if (errorCode is null) pending.TrySetResult(true);
            else pending.TrySetException(new GrblErrorException(errorCode.Value, GrblCodes.DescribeError(errorCode.Value)));

            // A $$ read finishes on the ok that follows the last setting line.
            if (_settingsBuffer is not null && _settingsRead is not null)
            {
                var snapshot = new Dictionary<int, double>(_settingsBuffer);
                _settingsBuffer = null;
                var tcs = _settingsRead;
                _settingsRead = null;
                tcs.TrySetResult(snapshot);
            }
            return;
        }

        if (errorCode is null) _streamer?.Acknowledge();
        else _streamer?.AcknowledgeError(errorCode.Value);
    }

    private void FailPendingCommands(Exception error)
    {
        lock (_sync)
        {
            while (_pendingCommands.Count > 0) _pendingCommands.Dequeue().TrySetException(error);
        }
        _settingsRead?.TrySetException(error);
        _settingsRead = null;
        _settingsBuffer = null;
    }

    // ---------------------------------------------------------------- status

    private void StartStatusPolling()
    {
        StopStatusPolling();
        _poller = new CancellationTokenSource();
        var token = _poller.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                var interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(StatusPollHz, 1, 20));
                try
                {
                    await Task.Delay(interval, token).ConfigureAwait(false);
                    var transport = _transport;
                    if (transport?.IsConnected == true)
                    {
                        await transport.WriteRealtimeAsync(Realtime.Status, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log(ConsoleDirection.Warning, $"Status poll failed: {ex.Message}");
                    return;
                }
            }
        }, CancellationToken.None);
    }

    private void StopStatusPolling()
    {
        _poller?.Cancel();
        _poller?.Dispose();
        _poller = null;
    }

    // -------------------------------------------------------------- commands

    /// <summary>Send a line and wait for its own acknowledgement.</summary>
    public async Task SendRawAsync(string line, CancellationToken cancellationToken = default)
    {
        var transport = _transport ?? throw new InvalidOperationException("Not connected.");
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return;

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync) _pendingCommands.Enqueue(tcs);

            Log(ConsoleDirection.Sent, trimmed);
            await transport.WriteAsync(Encoding.ASCII.GetBytes(trimmed + "\n"), cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                lock (_sync)
                {
                    // Drop it from the queue or every later acknowledgement is offset by one.
                    var remaining = _pendingCommands.Where(p => p != tcs).ToList();
                    _pendingCommands.Clear();
                    foreach (var p in remaining) _pendingCommands.Enqueue(p);
                }
                throw new TimeoutException($"The controller did not acknowledge '{trimmed}' within 10 seconds.");
            }

            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task RealtimeAsync(byte value, CancellationToken cancellationToken)
    {
        var transport = _transport ?? throw new InvalidOperationException("Not connected.");
        await transport.WriteRealtimeAsync(value, cancellationToken).ConfigureAwait(false);
    }

    public Task HomeAsync(CancellationToken cancellationToken = default) => SendRawAsync("$H", cancellationToken);

    public Task UnlockAsync(CancellationToken cancellationToken = default) => SendRawAsync("$X", cancellationToken);

    public async Task SoftResetAsync(CancellationToken cancellationToken = default)
    {
        await RealtimeAsync(Realtime.SoftReset, cancellationToken).ConfigureAwait(false);
        Log(ConsoleDirection.Info, "Soft reset sent.");

        _parser.Reset();
        _assembler.Reset();
        FailPendingCommands(new OperationCanceledException("Soft reset."));

        if (JobState is JobState.Running or JobState.Paused)
        {
            ResumeLine = (_streamer?.CompletedIndex ?? -1) + 1;
            _streamer?.Abort();
            SetJobState(JobState.Cancelled);
        }
    }

    public async Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        // Hold first so the machine decelerates rather than losing steps, then
        // reset to flush the buffer. Position is lost either way, which is why
        // the UI insists on re-homing afterwards.
        await RealtimeAsync(Realtime.FeedHold, cancellationToken).ConfigureAwait(false);
        await Task.Delay(60, cancellationToken).ConfigureAwait(false);
        await RealtimeAsync(Realtime.SpindleStop, cancellationToken).ConfigureAwait(false);
        await SoftResetAsync(cancellationToken).ConfigureAwait(false);
        IsHomed = false;
        Log(ConsoleDirection.Warning, "Emergency stop. Machine position is lost — home before running another job.");
    }

    public Task JogAsync(double dx, double dy, double dz, double feedMmMin, CancellationToken cancellationToken = default)
    {
        // $J= jogs are cancellable and do not disturb the modal state, which plain
        // G1 moves would.
        var sb = new StringBuilder("$J=G91G21");
        if (Math.Abs(dx) > 1e-9) sb.Append(CultureInfo.InvariantCulture, $"X{dx:0.###}");
        if (Math.Abs(dy) > 1e-9) sb.Append(CultureInfo.InvariantCulture, $"Y{dy:0.###}");
        if (Math.Abs(dz) > 1e-9) sb.Append(CultureInfo.InvariantCulture, $"Z{dz:0.###}");
        sb.Append(CultureInfo.InvariantCulture, $"F{Math.Max(1, feedMmMin):0}");
        return SendRawAsync(sb.ToString(), cancellationToken);
    }

    public Task CancelJogAsync(CancellationToken cancellationToken = default) =>
        RealtimeAsync(Realtime.JogCancel, cancellationToken);

    public Task SetWorkZeroAsync(bool x = true, bool y = true, bool z = true, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder("G10L20P1");
        if (x) sb.Append("X0");
        if (y) sb.Append("Y0");
        if (z) sb.Append("Z0");
        return SendRawAsync(sb.ToString(), cancellationToken);
    }

    public Task MoveToAsync(double x, double y, double feedMmMin, CancellationToken cancellationToken = default) =>
        SendRawAsync(string.Create(CultureInfo.InvariantCulture, $"G90G1X{x:0.###}Y{y:0.###}F{Math.Max(1, feedMmMin):0}"), cancellationToken);

    public Task SetFeedOverrideAsync(int percent, CancellationToken cancellationToken = default) =>
        ApplyOverrideAsync(percent, Status.Overrides.Feed, Realtime.FeedOverride100,
            Realtime.FeedOverridePlus10, Realtime.FeedOverrideMinus10,
            Realtime.FeedOverridePlus1, Realtime.FeedOverrideMinus1, cancellationToken);

    public Task SetSpindleOverrideAsync(int percent, CancellationToken cancellationToken = default) =>
        ApplyOverrideAsync(percent, Status.Overrides.Spindle, Realtime.SpindleOverride100,
            Realtime.SpindleOverridePlus10, Realtime.SpindleOverrideMinus10,
            Realtime.SpindleOverridePlus1, Realtime.SpindleOverrideMinus1, cancellationToken);

    public async Task SetRapidOverrideAsync(int percent, CancellationToken cancellationToken = default)
    {
        // Rapid override only has three steps in GRBL 1.1.
        var value = percent switch
        {
            >= 100 => Realtime.RapidOverride100,
            >= 50 => Realtime.RapidOverride50,
            _ => Realtime.RapidOverride25,
        };
        await RealtimeAsync(value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// GRBL has no "set override to N%" command — only nudges. Walk from the
    /// current value to the target in tens and ones.
    /// </summary>
    private async Task ApplyOverrideAsync(int target, int current, byte reset, byte plus10, byte minus10,
                                          byte plus1, byte minus1, CancellationToken cancellationToken)
    {
        target = Math.Clamp(target, 10, 200);

        if (target == 100)
        {
            await RealtimeAsync(reset, cancellationToken).ConfigureAwait(false);
            return;
        }

        var delta = target - current;
        var guard = 0;

        while (Math.Abs(delta) >= 10 && guard++ < 40)
        {
            await RealtimeAsync(delta > 0 ? plus10 : minus10, cancellationToken).ConfigureAwait(false);
            delta += delta > 0 ? -10 : 10;
        }

        while (Math.Abs(delta) >= 1 && guard++ < 60)
        {
            await RealtimeAsync(delta > 0 ? plus1 : minus1, cancellationToken).ConfigureAwait(false);
            delta += delta > 0 ? -1 : 1;
        }
    }

    // --------------------------------------------------------------- settings

    public async Task<IReadOnlyDictionary<int, double>> ReadSettingsAsync(CancellationToken cancellationToken = default)
    {
        _settingsBuffer = [];
        _settingsRead = new TaskCompletionSource<IReadOnlyDictionary<int, double>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs = _settingsRead;

        await SendRawAsync("$$", cancellationToken).ConfigureAwait(false);

        // SendRawAsync returns on the trailing ok, which is also what completes the read.
        if (tcs.Task.IsCompleted) return await tcs.Task.ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
        if (completed != tcs.Task) throw new TimeoutException("The controller did not return its settings.");

        return await tcs.Task.ConfigureAwait(false);
    }

    public Task WriteSettingAsync(int key, double value, CancellationToken cancellationToken = default)
    {
        _settings[key] = value;
        return SendRawAsync(string.Create(CultureInfo.InvariantCulture, $"${key}={value:0.###}"), cancellationToken);
    }

    // ------------------------------------------------------------------- jobs

    public async Task StartJobAsync(JobDefinition job, CancellationToken cancellationToken = default)
    {
        var transport = _transport ?? throw new InvalidOperationException("Not connected.");
        if (JobState.IsActive()) throw new InvalidOperationException("A job is already running.");

        SetJobState(JobState.Preparing);
        _job = job;
        _jobEstimate = job.EstimatedDuration;
        ResumeLine = -1;

        var lines = job.Lines;
        if (job.StartLine > 0 && job.StartLine < lines.Count)
        {
            // Re-state the modal groups before re-entering partway through, or the
            // resumed portion runs at whatever feed and power happen to be current.
            var preamble = GcodePreparer.BuildResumePreamble(lines, job.StartLine);
            Log(ConsoleDirection.Info,
                $"Resuming at line {job.StartLine + 1} with preamble: {string.Join(' ', preamble)}");
            lines = [.. preamble, .. lines.Skip(job.StartLine)];
        }

        var streamer = new GcodeStreamer(
            line => _ = transport.WriteAsync(Encoding.ASCII.GetBytes(line)).AsTask(),
            GcodeStreamer.DefaultRxBufferSize,
            stopOnError: true);

        streamer.Progress += _ => RaiseProgress();
        streamer.LineAcknowledged += (_, text) => _currentLine = text;

        streamer.Error += error => Log(ConsoleDirection.Error,
            $"Line {error.LineIndex + 1} rejected: {error.Info.Title}. {error.Info.Remedy}");

        streamer.Completed += progress =>
        {
            if (progress.State == StreamState.Faulted)
            {
                ResumeLine = progress.Acknowledged;
                SetJobState(JobState.Failed);
            }
            else if (JobState == JobState.Running)
            {
                SetJobState(JobState.Completed);
            }
            RaiseProgress();
        };

        _streamer = streamer;
        streamer.Load(lines);

        _jobStarted = DateTimeOffset.UtcNow;
        SetJobState(JobState.Running);
        Log(ConsoleDirection.Info, $"Starting '{job.Name}' — {lines.Count} lines, estimated {TimeEstimator.Format(job.EstimatedDuration)}.");

        streamer.Start();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task PauseJobAsync(CancellationToken cancellationToken = default)
    {
        if (JobState != JobState.Running) return;
        _streamer?.Pause();
        // The feed hold is what actually stops motion; pausing the stream alone
        // would let everything already buffered continue to run.
        await RealtimeAsync(Realtime.FeedHold, cancellationToken).ConfigureAwait(false);
        SetJobState(JobState.Paused);
        Log(ConsoleDirection.Info, "Job paused.");
    }

    public async Task ResumeJobAsync(CancellationToken cancellationToken = default)
    {
        if (JobState != JobState.Paused) return;
        await RealtimeAsync(Realtime.CycleStart, cancellationToken).ConfigureAwait(false);
        _streamer?.Resume();
        SetJobState(JobState.Running);
        Log(ConsoleDirection.Info, "Job resumed.");
    }

    public async Task StopJobAsync(CancellationToken cancellationToken = default)
    {
        if (!JobState.IsActive()) return;

        ResumeLine = (_streamer?.CompletedIndex ?? -1) + 1;
        _streamer?.Stop();

        await RealtimeAsync(Realtime.FeedHold, cancellationToken).ConfigureAwait(false);
        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        await RealtimeAsync(Realtime.SoftReset, cancellationToken).ConfigureAwait(false);

        _streamer?.Abort();
        _parser.Reset();
        _assembler.Reset();

        SetJobState(JobState.Cancelled);
        Log(ConsoleDirection.Warning, $"Job stopped at line {ResumeLine}. Machine position is lost — home before continuing.");
        IsHomed = false;
    }

    public async Task FrameAsync(IReadOnlyList<Polyline> outline, FramingOptions options, CancellationToken cancellationToken = default)
    {
        if (JobState.IsActive()) throw new InvalidOperationException("Cannot frame while a job is running.");

        var lines = FramingGenerator.Build(outline, options, Profile);
        Log(ConsoleDirection.Info, $"Framing: {FramingGenerator.Describe(outline, options)}");

        foreach (var line in lines)
        {
            if (line.StartsWith(';')) continue;
            cancellationToken.ThrowIfCancellationRequested();
            await SendRawAsync(line, cancellationToken).ConfigureAwait(false);
        }
    }

    // ----------------------------------------------------------------- events

    private void SetConnection(ConnectionState state)
    {
        if (Connection == state) return;
        Connection = state;
        ConnectionChanged?.Invoke(state);
    }

    private void SetJobState(JobState state)
    {
        if (JobState == state) return;
        JobState = state;
        RaiseProgress();
    }

    private void RaiseProgress()
    {
        var snapshot = _streamer?.Snapshot() ?? default;
        var elapsed = JobState.IsActive() || JobState.IsTerminal()
            ? DateTimeOffset.UtcNow - _jobStarted
            : TimeSpan.Zero;

        // Blend the static estimate with observed throughput: the static figure is
        // right at the start when nothing has run yet, measured rate is right later.
        var fraction = snapshot.Total > 0 ? (double)snapshot.Acknowledged / snapshot.Total : 0;
        TimeSpan remaining;
        if (fraction <= 0.02)
        {
            remaining = _jobEstimate;
        }
        else
        {
            var measured = TimeSpan.FromSeconds(elapsed.TotalSeconds / fraction * (1 - fraction));
            var predicted = TimeSpan.FromSeconds(_jobEstimate.TotalSeconds * (1 - fraction));
            var blend = Math.Min(1.0, fraction * 4);
            remaining = TimeSpan.FromSeconds(measured.TotalSeconds * blend + predicted.TotalSeconds * (1 - blend));
        }

        Progress = new JobProgress(JobState, snapshot.Sent, snapshot.Acknowledged, snapshot.Total,
                                   elapsed, remaining, _currentLine);
        ProgressChanged?.Invoke(Progress);
    }

    private void Log(ConsoleDirection direction, string text) =>
        ConsoleOutput?.Invoke(new ConsoleEntry(DateTimeOffset.UtcNow, direction, text));

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _commandLock.Dispose();
    }
}

public sealed class GrblErrorException(int code, GrblCodeInfo info)
    : Exception($"error:{code} — {info.Title}. {info.Remedy}")
{
    public int Code { get; } = code;
    public GrblCodeInfo Info { get; } = info;
}
