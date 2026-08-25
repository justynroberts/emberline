using Emberline.Core.Geometry;
using Emberline.Core.Jobs;
using Emberline.Core.Machines;
using Emberline.GCode.Grbl;
using Emberline.Transport;

namespace Emberline.Devices;

public enum ConnectionState { Disconnected, Connecting, Connected, Reconnecting, Faulted }

public enum ConsoleDirection { Sent, Received, Info, Warning, Error }

public readonly record struct ConsoleEntry(DateTimeOffset Timestamp, ConsoleDirection Direction, string Text);

/// <summary>
/// What the application talks to. Transport, firmware dialect and vendor quirks
/// all live behind this, so the same UI drives a USB machine, a Wi-Fi machine and
/// the simulator without a single conditional.
/// </summary>
public interface ILaserDevice : IAsyncDisposable
{
    MachineProfile Profile { get; }
    ConnectionState Connection { get; }
    GrblStatus Status { get; }
    JobState JobState { get; }
    JobProgress Progress { get; }

    /// <summary>Settings read from the controller with <c>$$</c>. Empty until a read completes.</summary>
    IReadOnlyDictionary<int, double> Settings { get; }

    /// <summary>True once the machine has completed a homing cycle this session.</summary>
    bool IsHomed { get; }

    event Action<ConnectionState>? ConnectionChanged;
    event Action<GrblStatus>? StatusChanged;
    event Action<JobProgress>? ProgressChanged;
    event Action<ConsoleEntry>? ConsoleOutput;

    /// <summary>Raised for an error or alarm, with a decoded explanation and remedy.</summary>
    event Action<GrblCodeInfo, bool>? Fault;

    Task ConnectAsync(ITransport transport, CancellationToken cancellationToken = default);
    Task DisconnectAsync();

    Task HomeAsync(CancellationToken cancellationToken = default);
    Task UnlockAsync(CancellationToken cancellationToken = default);

    /// <summary>Soft reset (0x18). Discards the controller buffer and abandons any job.</summary>
    Task SoftResetAsync(CancellationToken cancellationToken = default);

    /// <summary>Feed hold then soft reset — the fastest safe stop available over the wire.</summary>
    Task EmergencyStopAsync(CancellationToken cancellationToken = default);

    /// <summary>Relative jog using <c>$J=</c>, so it can be cancelled without touching the job state.</summary>
    Task JogAsync(double dx, double dy, double dz, double feedMmMin, CancellationToken cancellationToken = default);

    Task CancelJogAsync(CancellationToken cancellationToken = default);

    /// <summary>Zero the work origin on the named axes, or all of them when none are given.</summary>
    Task SetWorkZeroAsync(bool x = true, bool y = true, bool z = true, CancellationToken cancellationToken = default);

    Task MoveToAsync(double x, double y, double feedMmMin, CancellationToken cancellationToken = default);

    Task StartJobAsync(JobDefinition job, CancellationToken cancellationToken = default);
    Task PauseJobAsync(CancellationToken cancellationToken = default);
    Task ResumeJobAsync(CancellationToken cancellationToken = default);
    Task StopJobAsync(CancellationToken cancellationToken = default);

    /// <summary>Trace the job outline at low or zero power so the operator can align the work.</summary>
    Task FrameAsync(IReadOnlyList<Polyline> outline, FramingOptions options, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<int, double>> ReadSettingsAsync(CancellationToken cancellationToken = default);
    Task WriteSettingAsync(int key, double value, CancellationToken cancellationToken = default);

    /// <summary>Send a raw line, as typed into the console.</summary>
    Task SendRawAsync(string line, CancellationToken cancellationToken = default);

    Task SetFeedOverrideAsync(int percent, CancellationToken cancellationToken = default);
    Task SetSpindleOverrideAsync(int percent, CancellationToken cancellationToken = default);
    Task SetRapidOverrideAsync(int percent, CancellationToken cancellationToken = default);
}
