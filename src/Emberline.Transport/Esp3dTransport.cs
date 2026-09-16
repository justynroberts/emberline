using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;

namespace Emberline.Transport;

/// <summary>
/// The ESP3D web interface built into Grbl_Esp32 — commands in over HTTP, replies
/// out over a WebSocket.
///
/// Neither half works alone, and each fails in a way that looks like success.
/// Commands written to the WebSocket are silently ignored: the socket is output
/// only. <c>GET /command?commandText=</c> is accepted with <c>200</c> and an
/// empty body, because GRBL's reply is not in the response — it arrives on the
/// WebSocket as a binary frame. So a plain WebSocket transport connects and hears
/// nothing, and a plain HTTP transport connects and receives blank lines. This
/// transport joins the two, which is what the controller's own web page does.
///
/// Recorded against a BlazeX with Grbl_Esp32 1.3a (2021-11-03), which reports
/// <c>webcommunication: Sync: 81</c> from <c>[ESP800]</c>, greets each socket
/// with <c>CURRENT_ID:n</c> and <c>ACTIVE_ID:n</c> text frames, and sends
/// <c>PING:n</c> text frames as a heartbeat. Text frames are the web interface
/// talking; binary frames are GRBL.
///
/// Everything goes through one small web server on an ESP32, and that server has
/// been seen to stop answering HTTP — while still accepting connections — after a
/// burst of overlapping requests and WebSocket sessions that dropped without a
/// close. So lines are sent strictly one at a time, status polls that would pile
/// up behind a slow one are dropped rather than queued, and disconnecting always
/// sends a proper WebSocket close.
/// </summary>
public sealed class Esp3dTransport : TransportBase
{
    private const string CommandPath = "/command?commandText=";

    private readonly string _host;
    private readonly int _httpPort;
    private readonly int _fallbackWebSocketPort;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lineLock = new(1, 1);
    private readonly StringBuilder _lineBuffer = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _readLoop;
    private Task? _readTask;
    private volatile bool _closing;
    private int _statusInFlight;
    private int _webSocketPort;
    private TimeSpan _handshakeTime;

    public Esp3dTransport(string host, int httpPort = 80, int webSocketPort = 81)
    {
        _host = host;
        _httpPort = httpPort;
        _fallbackWebSocketPort = webSocketPort;
        _webSocketPort = webSocketPort;

        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            // One for the current line, one so a feed hold is not stuck behind it.
            // More than that is how the controller's web server was made to hang.
            MaxConnectionsPerServer = 2,
        })
        {
            BaseAddress = new Uri($"http://{host}:{httpPort}"),
            // Round trips of well over a second have been measured on a weak
            // 2.4 GHz link. Six seconds, the plain HTTP transport's figure, gives up
            // on requests that were going to arrive.
            Timeout = TimeSpan.FromSeconds(12),
        };
    }

    public override TransportKind Kind => TransportKind.Esp3d;

    public override string Description => string.Create(CultureInfo.InvariantCulture,
        $"ESP3D at {_host} (commands HTTP :{_httpPort}, replies WebSocket :{_webSocketPort}, first answer in {_handshakeTime.TotalMilliseconds:0} ms)");

    public override bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <inheritdoc cref="ITransport.SupportsStreaming"/>
    public override bool SupportsStreaming => false;

    /// <summary>The firmware's own description of itself, from <c>[ESP800]</c>.</summary>
    public string? FirmwareReport { get; private set; }

    /// <summary>The id the web interface gave this socket, from <c>CURRENT_ID</c>.</summary>
    public int? ClientId { get; private set; }

    public override async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        // [ESP800] is answered by the web interface itself and never reaches GRBL,
        // so it is safe to send to a machine that is part-way through anything. It
        // also says which port the reply socket is on, rather than assuming 81.
        var stopwatch = Stopwatch.StartNew();
        var report = await GetAsync("/command?plain=%5BESP800%5D", cancellationToken).ConfigureAwait(false)
            ?? throw new IOException($"Nothing answered at http://{_host}:{_httpPort}/. Check the address, and that the machine is on the same network.");
        _handshakeTime = stopwatch.Elapsed;

        if (!report.Contains("FW version", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"http://{_host}:{_httpPort}/ answers, but not as an ESP3D controller.");
        }
        FirmwareReport = report.Trim();

        if (ReadField(report, "authentication") is { } auth && auth.StartsWith("yes", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("This controller's web interface requires a login, which Emberline does not support yet.");
        }

        _webSocketPort = ParseWebSocketPort(report) ?? _fallbackWebSocketPort;

        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("arduino");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await socket.ConnectAsync(new Uri($"ws://{_host}:{_webSocketPort}/"), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                throw new IOException($"The controller's web interface answered, but its reply socket on port {_webSocketPort} did not.", ex);
            }
        }

        _closing = false;
        _socket = socket;
        _readLoop = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoopAsync(socket, _readLoop.Token), CancellationToken.None);
    }

    /// <summary>
    /// The <c>webcommunication</c> field of an <c>[ESP800]</c> report, e.g.
    /// <c>Sync: 81:192.168.68.91</c>. Only Sync mode has been seen; Async puts the
    /// socket somewhere else and is refused rather than guessed at.
    /// </summary>
    public static int? ParseWebSocketPort(string report)
    {
        var field = ReadField(report, "webcommunication");
        if (field is null) return null;

        var parts = field.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null;

        if (!parts[0].Equals("Sync", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"This controller uses ESP3D '{parts[0]}' web communication, which Emberline has not been tested against.");
        }

        return int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : null;
    }

    private static string? ReadField(string report, string name)
    {
        // "FW version:1.3a (20211103) # FW target:grbl-embedded # ... # webcommunication: Sync: 81:ip"
        foreach (var segment in report.Split('#'))
        {
            var colon = segment.IndexOf(':');
            if (colon < 0) continue;
            if (segment[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return segment[(colon + 1)..].Trim();
            }
        }
        return null;
    }

    private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        var text = new StringBuilder();
        Exception? error = null;

        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // GRBL output. Chunk boundaries do not matter; the device reassembles lines.
                    if (result.Count > 0) RaiseData(buffer.AsMemory(0, result.Count).ToArray());
                    continue;
                }

                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                HandleInterfaceMessage(text.ToString());
                text.Clear();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }

        // A close this side asked for is not a lost link.
        if (!token.IsCancellationRequested && !_closing) RaiseDisconnected(error);
    }

    /// <summary>Text frames are the web interface's own bookkeeping, not GRBL output.</summary>
    private void HandleInterfaceMessage(string message)
    {
        if (TryReadId(message, "CURRENT_ID:", out var current))
        {
            ClientId = current;
            return;
        }

        if (TryReadId(message, "ACTIVE_ID:", out var active))
        {
            // The web interface treats the most recent socket as the one in charge,
            // and its own page backs off when it is not. Somebody opening that page
            // in a browser does this; it is worth saying rather than going quiet.
            if (ClientId is { } mine && active != mine)
            {
                RaiseData(Encoding.ASCII.GetBytes(
                    $"[MSG:Another web client (id {active}) has connected to this controller; its replies may no longer reach Emberline]\n"));
            }
            return;
        }

        if (message.StartsWith("PING:", StringComparison.Ordinal)) return;

        // Anything unrecognised is shown rather than swallowed. It is rare, and a
        // message nobody sees is a message that might have mattered.
        RaiseData(Encoding.UTF8.GetBytes($"[MSG:{message.Trim()}]\n"));
    }

    private static bool TryReadId(string message, string prefix, out int id)
    {
        id = 0;
        return message.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(message.AsSpan(prefix.Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    public override async Task DisconnectAsync()
    {
        var socket = _socket;
        var loop = _readLoop;
        var readTask = _readTask;
        _socket = null;
        _readLoop = null;
        _readTask = null;
        _closing = true;

        if (socket is not null)
        {
            // Always close properly — sessions that simply vanished are part of what
            // left this controller's web server unresponsive — and close before
            // stopping the read loop. Cancelling a pending receive aborts a
            // ClientWebSocket outright, so cancelling first means no close frame is
            // ever sent. CloseOutputAsync rather than CloseAsync, because CloseAsync
            // would wait for the reply on a second receive while the loop is still
            // receiving. The loop sees the controller's close and ends by itself.
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", closeTimeout.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // A controller that vanishes mid-close is normal, not exceptional.
            }

            if (readTask is not null)
            {
                await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
            }
        }

        if (loop is not null)
        {
            await loop.CancelAsync().ConfigureAwait(false);
            loop.Dispose();
        }

        socket?.Dispose();

        lock (_lineBuffer) _lineBuffer.Clear();
        ClientId = null;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Not connected.");

        var lines = new List<string>();
        lock (_lineBuffer)
        {
            foreach (var ch in Encoding.ASCII.GetString(data.Span))
            {
                if (ch is '\n' or '\r')
                {
                    if (_lineBuffer.Length > 0) lines.Add(_lineBuffer.ToString());
                    _lineBuffer.Clear();
                }
                else
                {
                    _lineBuffer.Append(ch);
                }
            }
        }

        foreach (var line in lines)
        {
            // Strictly one at a time: GRBL must see lines in order, and this web
            // server does not cope with a queue of overlapping requests.
            await _lineLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var sent = await GetAsync(CommandPath + Uri.EscapeDataString(line), cancellationToken).ConfigureAwait(false);
                // No reply is fabricated on failure. The acknowledgement comes over the
                // socket or not at all, and pretending otherwise would tell the caller
                // the controller said something it did not.
                if (sent is null) throw new IOException($"The controller did not accept '{line}' — the web request failed.");
            }
            finally
            {
                _lineLock.Release();
            }
        }
    }

    public override async ValueTask WriteRealtimeAsync(byte value, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Not connected.");

        // Percent-encoded as the raw byte. Uri.EscapeDataString would send the
        // override bytes above 0x7F as two-byte UTF-8, which GRBL would read as two
        // unrelated commands.
        var path = string.Create(CultureInfo.InvariantCulture, $"{CommandPath}%{value:X2}");

        if (value == (byte)'?')
        {
            // Status polls are best-effort. If the last one has not come back, a new
            // one would only queue behind it on a server that handles one request at a
            // time — so skip it. A failed poll is not reported either: on a lossy link
            // some will fail, and the next one is a fraction of a second away.
            if (Interlocked.CompareExchange(ref _statusInFlight, 1, 0) != 0) return;
            try
            {
                await GetAsync(path, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _statusInFlight, 0);
            }
            return;
        }

        // Everything else — feed hold, resume, reset, jog cancel, overrides — does
        // not wait behind a line in progress, and a failure is an error: a stop
        // that did not arrive must not look like one that did.
        if (await GetAsync(path, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new IOException($"Real-time command 0x{value:X2} did not reach the controller.");
        }
    }

    /// <summary>The response body, or null when the request did not succeed.</summary>
    private async Task<string?> GetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(pathAndQuery, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
