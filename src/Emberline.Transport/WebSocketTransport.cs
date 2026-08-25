using System.Net.WebSockets;

namespace Emberline.Transport;

/// <summary>
/// WebSocket console, as exposed by FluidNC and ESP3D-based firmware — typically
/// <c>ws://host:81/</c>. Useful when the board's telnet port is occupied by its
/// own web UI.
/// </summary>
public sealed class WebSocketTransport : TransportBase
{
    private readonly Uri _uri;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _readLoop;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public WebSocketTransport(string host, int port = 81, string path = "/")
        : this(new Uri($"ws://{host}:{port}{path}"))
    {
    }

    public WebSocketTransport(Uri uri) => _uri = uri;

    public override TransportKind Kind => TransportKind.WebSocket;
    public override string Description => _uri.ToString();
    public override bool IsConnected => _socket?.State == WebSocketState.Open;

    public override async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var socket = new ClientWebSocket();
        // ESP3D negotiates a subprotocol; asking for it is harmless when the
        // firmware does not implement one.
        socket.Options.AddSubProtocol("arduino");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));

        await socket.ConnectAsync(_uri, timeout.Token).ConfigureAwait(false);

        _socket = socket;
        _readLoop = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(socket, _readLoop.Token), CancellationToken.None);
    }

    private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        Exception? error = null;
        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.Count > 0) RaiseData(buffer.AsMemory(0, result.Count).ToArray());
            }
        }
        catch (OperationCanceledException)
        {
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

        if (_socket is { } socket)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeTimeout.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // A controller that vanishes mid-close is normal, not exceptional.
            }
            socket.Dispose();
            _socket = null;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var socket = _socket ?? throw new InvalidOperationException("Not connected.");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
