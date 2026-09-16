using Emberline.Core.Machines;
using Emberline.Transport;

namespace Emberline.Devices;

/// <summary>
/// The BlazeX M5 Pro, Emberline's reference machine.
///
/// Phase 1 is USB, and over USB the board speaks ordinary GRBL 1.1 — so this
/// starts as <see cref="GrblDevice"/> plus the small handling differences the
/// board actually needs. Phase 2 is native Wi-Fi.
///
/// **On the Wi-Fi protocol.** Nothing here is reverse-engineered guesswork about a
/// machine that commands a ten-watt laser. It was the plan to assume telnet on 23
/// and a WebSocket on 81, and that assumption was wrong: probing a real unit found
/// Grbl_Esp32 1.3a behind ESP3D, no telnet console, and a WebSocket that only
/// carries replies. The network path is therefore <see cref="Esp3dTransport"/>,
/// which is generic ESP3D rather than anything BlazeX-specific. Any behaviour that
/// genuinely is BlazeX-specific belongs in this class and nowhere else, so that
/// Core never learns a vendor's name.
/// </summary>
public sealed class BlazeXDevice : ILaserDevice
{
    private readonly GrblDevice _inner;

    public BlazeXDevice(MachineProfile profile) => _inner = new GrblDevice(profile);

    public MachineProfile Profile => _inner.Profile;
    public ConnectionState Connection => _inner.Connection;
    public GCode.Grbl.GrblStatus Status => _inner.Status;
    public Core.Jobs.JobState JobState => _inner.JobState;
    public Core.Jobs.JobProgress Progress => _inner.Progress;
    public IReadOnlyDictionary<int, double> Settings => _inner.Settings;
    public bool IsHomed => _inner.IsHomed;
    public bool CanStreamJobs => _inner.CanStreamJobs;
    public int ResumeLine => _inner.ResumeLine;
    public string? FirmwareVersion => _inner.FirmwareVersion;

    public event Action<ConnectionState>? ConnectionChanged
    {
        add => _inner.ConnectionChanged += value;
        remove => _inner.ConnectionChanged -= value;
    }

    public event Action<GCode.Grbl.GrblStatus>? StatusChanged
    {
        add => _inner.StatusChanged += value;
        remove => _inner.StatusChanged -= value;
    }

    public event Action<Core.Jobs.JobProgress>? ProgressChanged
    {
        add => _inner.ProgressChanged += value;
        remove => _inner.ProgressChanged -= value;
    }

    public event Action<ConsoleEntry>? ConsoleOutput
    {
        add => _inner.ConsoleOutput += value;
        remove => _inner.ConsoleOutput -= value;
    }

    public event Action<GCode.Grbl.GrblCodeInfo, bool>? Fault
    {
        add => _inner.Fault += value;
        remove => _inner.Fault -= value;
    }

    public async Task ConnectAsync(ITransport transport, CancellationToken cancellationToken = default)
    {
        await _inner.ConnectAsync(transport, cancellationToken).ConfigureAwait(false);

        if (transport.Kind is TransportKind.Tcp or TransportKind.WebSocket)
        {
            // Wireless links do not tolerate the same poll rate as USB: every status
            // request competes with job data for the same TCP window, and on a busy
            // 2.4 GHz network six hertz is already enough to cause visible stutter.
            _inner.StatusPollHz = 4;
        }
        else if (transport.Kind is TransportKind.Esp3d)
        {
            // Over ESP3D each poll is a whole web request to a server that handles
            // one at a time, on a link measured at hundreds of milliseconds a round
            // trip. Twice a second keeps the position readout live without queueing.
            _inner.StatusPollHz = 2;
        }
    }

    public Task DisconnectAsync() => _inner.DisconnectAsync();
    public Task HomeAsync(CancellationToken ct = default) => _inner.HomeAsync(ct);
    public Task UnlockAsync(CancellationToken ct = default) => _inner.UnlockAsync(ct);
    public Task SoftResetAsync(CancellationToken ct = default) => _inner.SoftResetAsync(ct);
    public Task EmergencyStopAsync(CancellationToken ct = default) => _inner.EmergencyStopAsync(ct);
    public Task JogAsync(double dx, double dy, double dz, double feed, CancellationToken ct = default) => _inner.JogAsync(dx, dy, dz, feed, ct);
    public Task CancelJogAsync(CancellationToken ct = default) => _inner.CancelJogAsync(ct);
    public Task SetWorkZeroAsync(bool x = true, bool y = true, bool z = true, CancellationToken ct = default) => _inner.SetWorkZeroAsync(x, y, z, ct);
    public Task MoveToAsync(double x, double y, double feed, CancellationToken ct = default) => _inner.MoveToAsync(x, y, feed, ct);
    public Task StartJobAsync(Core.Jobs.JobDefinition job, CancellationToken ct = default) => _inner.StartJobAsync(job, ct);
    public Task PauseJobAsync(CancellationToken ct = default) => _inner.PauseJobAsync(ct);
    public Task ResumeJobAsync(CancellationToken ct = default) => _inner.ResumeJobAsync(ct);
    public Task StopJobAsync(CancellationToken ct = default) => _inner.StopJobAsync(ct);
    public Task FrameAsync(IReadOnlyList<Core.Geometry.Polyline> outline, Core.Jobs.FramingOptions options, CancellationToken ct = default) => _inner.FrameAsync(outline, options, ct);
    public Task<IReadOnlyDictionary<int, double>> ReadSettingsAsync(CancellationToken ct = default) => _inner.ReadSettingsAsync(ct);
    public Task WriteSettingAsync(int key, double value, CancellationToken ct = default) => _inner.WriteSettingAsync(key, value, ct);
    public Task SendRawAsync(string line, CancellationToken ct = default) => _inner.SendRawAsync(line, ct);
    public Task SetFeedOverrideAsync(int percent, CancellationToken ct = default) => _inner.SetFeedOverrideAsync(percent, ct);
    public Task SetSpindleOverrideAsync(int percent, CancellationToken ct = default) => _inner.SetSpindleOverrideAsync(percent, ct);
    public Task SetRapidOverrideAsync(int percent, CancellationToken ct = default) => _inner.SetRapidOverrideAsync(percent, ct);
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
