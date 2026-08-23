namespace OpenBurn.Transport;

public enum TransportKind { Serial, Tcp, WebSocket, Http, Virtual }

/// <summary>
/// A byte pipe to a controller.
///
/// Deliberately knows nothing about GRBL. Keeping the wire separate from the
/// protocol is what lets the same job engine drive a USB machine, a Wi-Fi machine
/// and the simulator without a single conditional — which is the whole point of
/// the device abstraction in the PRD.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    TransportKind Kind { get; }

    /// <summary>Human-readable endpoint, e.g. "/dev/tty.usbserial-1420 @ 115200" or "192.168.1.42:23".</summary>
    string Description { get; }

    bool IsConnected { get; }

    /// <summary>Raw bytes as they arrive. Chunk boundaries are arbitrary — the caller must reassemble lines.</summary>
    event Action<ReadOnlyMemory<byte>>? DataReceived;

    /// <summary>Fired when the link drops. A null argument means a clean, requested close.</summary>
    event Action<Exception?>? Disconnected;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync();

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write a real-time byte ahead of anything queued.
    ///
    /// A feed hold that waits behind forty kilobytes of buffered raster is not a
    /// feed hold, so this must never sit in the same queue as G-code.
    /// </summary>
    ValueTask WriteRealtimeAsync(byte value, CancellationToken cancellationToken = default);
}

/// <summary>Base class handling the event plumbing every transport repeats.</summary>
public abstract class TransportBase : ITransport
{
    private int _disposed;

    public abstract TransportKind Kind { get; }
    public abstract string Description { get; }
    public abstract bool IsConnected { get; }

    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<Exception?>? Disconnected;

    public abstract Task ConnectAsync(CancellationToken cancellationToken = default);
    public abstract Task DisconnectAsync();
    public abstract ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    public virtual ValueTask WriteRealtimeAsync(byte value, CancellationToken cancellationToken = default) =>
        WriteAsync(new[] { value }, cancellationToken);

    protected void RaiseData(ReadOnlyMemory<byte> data) => DataReceived?.Invoke(data);

    protected void RaiseDisconnected(Exception? error) => Disconnected?.Invoke(error);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        catch
        {
            // Disposal must not throw — the caller is already tearing down.
        }
        GC.SuppressFinalize(this);
    }
}
