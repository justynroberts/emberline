using System.Net.Sockets;

namespace OpenBurn.Transport;

/// <summary>
/// Raw TCP, which is how Grbl_ESP32, FluidNC and most Wi-Fi laser controllers
/// expose their console — usually on port 23.
/// </summary>
public sealed class TcpTransport : TransportBase
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readLoop;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public TcpTransport(string host, int port = 23)
    {
        _host = host;
        _port = port;
    }

    public override TransportKind Kind => TransportKind.Tcp;
    public override string Description => $"{_host}:{_port}";
    public override bool IsConnected => _client?.Connected == true;

    /// <summary>Round-trip time measured at connect. Above about 150 ms, streaming throughput suffers.</summary>
    public TimeSpan ConnectLatency { get; private set; }

    public override async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var started = DateTimeOffset.UtcNow;
        var client = new TcpClient
        {
            // Nagle batches small writes, which is exactly wrong here: real-time
            // bytes and short G-code lines must go out the instant they are written.
            NoDelay = true,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));

        await client.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
        ConnectLatency = DateTimeOffset.UtcNow - started;

        _client = client;
        _stream = client.GetStream();
        _readLoop = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_stream, _readLoop.Token), CancellationToken.None);
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        Exception? error = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read <= 0) break;
                RaiseData(buffer.AsMemory(0, read).ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            // Requested close.
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (!token.IsCancellationRequested) RaiseDisconnected(error);
    }

    public override async Task DisconnectAsync()
    {
        if (_readLoop is { } cts)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
            _readLoop = null;
        }

        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected.");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
