using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Emberline.Core.Jobs;
using Emberline.Core.Machines;
using Emberline.Devices;
using Emberline.Transport;
using Emberline.VirtualLaser;
using Xunit;

namespace Emberline.Devices.Tests;

/// <summary>
/// The ESP3D link — commands in over HTTP, replies out over a WebSocket.
///
/// The fake controller reproduces what a real BlazeX running Grbl_Esp32 1.3a was
/// recorded doing, and nothing it was not: <c>[ESP800]</c> answered over HTTP,
/// <c>commandText</c> accepted with an empty body, GRBL's reply pushed to the
/// socket as a binary frame, <c>CURRENT_ID</c>/<c>ACTIVE_ID</c>/<c>PING</c> sent as
/// text frames, and anything written to the socket ignored. The simulator stands
/// behind it as GRBL, so these tests exercise the real device code end to end.
/// </summary>
public class Esp3dTransportTests
{
    /// <summary>The report the real controller gave, with the socket port swapped for the fake's.</summary>
    private const string RecordedReport =
        "FW version:1.3a (20211103) # FW target:grbl-embedded  # FW HW:Direct SD  # primary sd:/sd " +
        "# secondary sd:none # authentication:no # webcommunication: Sync: 81:192.168.68.91 # hostname:grblesp # axis:3";

    private sealed class FakeEsp3d : IAsyncDisposable
    {
        private readonly HttpListener _http = new();
        private readonly HttpListener _ws = new();
        private readonly VirtualTransport _grbl = new(new VirtualLaserOptions(), realTimeScale: 200);
        private readonly List<WebSocket> _sockets = [];
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly List<byte[]> _commands = [];
        private readonly List<byte> _socketInbound = [];
        private readonly CancellationTokenSource _stop = new();
        private int _nextId = 1;

        public FakeEsp3d(bool requireLogin = false)
        {
            HttpPort = FreePort();
            WebSocketPort = FreePort();
            _http.Prefixes.Add($"http://127.0.0.1:{HttpPort}/");
            _ws.Prefixes.Add($"http://127.0.0.1:{WebSocketPort}/");
            _http.Start();
            _ws.Start();

            Report = RecordedReport
                .Replace("Sync: 81:192.168.68.91", $"Sync: {WebSocketPort}:127.0.0.1")
                .Replace("authentication:no", requireLogin ? "authentication:yes" : "authentication:no");

            _grbl.DataReceived += data => _ = BroadcastAsync(data.ToArray());
            _grbl.ConnectAsync().GetAwaiter().GetResult();

            _ = Task.Run(ServeHttpAsync);
            _ = Task.Run(ServeSocketsAsync);
        }

        public int HttpPort { get; }
        public int WebSocketPort { get; }
        public string Report { get; }
        public bool CleanCloseReceived { get; private set; }

        /// <summary>Every commandText received, decoded to the exact bytes.</summary>
        public byte[][] Commands { get { lock (_commands) return [.. _commands]; } }

        public IReadOnlyList<string> CommandLines =>
            Commands.Where(c => c.Length > 1).Select(c => Encoding.ASCII.GetString(c)).ToList();

        /// <summary>Anything a client wrote to the socket. The real firmware ignores it.</summary>
        public byte[] SocketInbound { get { lock (_socketInbound) return [.. _socketInbound]; } }

        /// <summary>Stop answering HTTP while leaving the socket up, as the real controller once did.</summary>
        public void StopAnsweringCommands() => _http.Stop();

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private async Task ServeHttpAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _http.GetContextAsync(); }
                catch { return; }

                var raw = context.Request.RawUrl ?? "";
                var query = raw.Contains('?') ? raw[(raw.IndexOf('?') + 1)..] : "";
                var body = "";

                if (query.StartsWith("plain=", StringComparison.Ordinal) &&
                    Encoding.ASCII.GetString(PercentDecode(query["plain=".Length..])) == "[ESP800]")
                {
                    body = Report;
                }
                else if (query.StartsWith("commandText=", StringComparison.Ordinal))
                {
                    var bytes = PercentDecode(query["commandText=".Length..]);
                    lock (_commands) _commands.Add(bytes);

                    // A single real-time byte goes straight in; anything else is a line.
                    var isRealtime = bytes.Length == 1 && (bytes[0] is (byte)'?' or (byte)'!' or (byte)'~' or 0x18 || bytes[0] >= 0x80);
                    await _grbl.WriteAsync(isRealtime ? bytes : [.. bytes, (byte)'\n']);
                }

                var payload = Encoding.ASCII.GetBytes(body);
                context.Response.StatusCode = 200;
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload);
                context.Response.Close();
            }
        }

        /// <summary>Raw percent-decoding to bytes: %85 is the byte 0x85, not a UTF-8 sequence.</summary>
        private static byte[] PercentDecode(string text)
        {
            var bytes = new List<byte>();
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '%' && i + 2 < text.Length)
                {
                    bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                    i += 2;
                }
                else
                {
                    bytes.Add(text[i] == '+' ? (byte)' ' : (byte)text[i]);
                }
            }
            return [.. bytes];
        }

        private async Task ServeSocketsAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _ws.GetContextAsync(); }
                catch { return; }

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                var accepted = await context.AcceptWebSocketAsync("arduino");
                var socket = accepted.WebSocket;
                var id = Interlocked.Increment(ref _nextId) - 1;
                lock (_sockets) _sockets.Add(socket);

                foreach (var text in new[] { $"CURRENT_ID:{id}", $"ACTIVE_ID:{id}", $"PING:{id}" })
                {
                    await SendAsync(socket, Encoding.ASCII.GetBytes(text), WebSocketMessageType.Text);
                }

                _ = Task.Run(async () =>
                {
                    var buffer = new byte[1024];
                    try
                    {
                        while (socket.State == WebSocketState.Open)
                        {
                            var result = await socket.ReceiveAsync(buffer, _stop.Token);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                CleanCloseReceived = true;
                                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                                break;
                            }
                            lock (_socketInbound) _socketInbound.AddRange(buffer[..result.Count]);
                        }
                    }
                    catch
                    {
                        // A client that vanishes is the client's problem.
                    }
                    lock (_sockets) _sockets.Remove(socket);
                });
            }
        }

        private async Task BroadcastAsync(byte[] data)
        {
            WebSocket[] sockets;
            lock (_sockets) sockets = [.. _sockets];
            foreach (var socket in sockets) await SendAsync(socket, data, WebSocketMessageType.Binary);
        }

        private async Task SendAsync(WebSocket socket, byte[] data, WebSocketMessageType type)
        {
            await _sendLock.WaitAsync();
            try
            {
                if (socket.State == WebSocketState.Open) await socket.SendAsync(data, type, true, CancellationToken.None);
            }
            catch
            {
                // Closing mid-send is normal.
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try { _http.Stop(); } catch { }
            try { _ws.Stop(); } catch { }
            await _grbl.DisconnectAsync();
        }
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(15);
        }
        Assert.Fail($"Condition not met within {timeoutMs} ms.");
    }

    // A deliberately wrong fallback socket port: connecting only works if the
    // transport asks the controller where its socket is.
    private static Esp3dTransport Transport(FakeEsp3d fake) => new("127.0.0.1", fake.HttpPort, webSocketPort: 1);

    [Fact]
    public async Task ConnectsByAskingTheWebInterfaceWhereItsReplySocketIs()
    {
        await using var fake = new FakeEsp3d();
        await using var transport = Transport(fake);

        await transport.ConnectAsync();

        Assert.True(transport.IsConnected);
        await WaitUntil(() => transport.ClientId == 1);
        Assert.Contains("grbl-embedded", transport.FirmwareReport);
        Assert.False(transport.SupportsStreaming);
    }

    [Fact]
    public async Task CommandsGoOverHttpAndRepliesComeBackOnTheSocket()
    {
        await using var fake = new FakeEsp3d();
        var device = new GrblDevice(MachineProfile.BlazeXM5Pro()) { StatusPollHz = 10 };
        await using (device)
        {
            await device.ConnectAsync(Transport(fake));

            // $$ was sent as a web request and every setting arrived over the socket.
            Assert.Contains("$$", fake.CommandLines);
            Assert.True(device.Settings.Count > 20);
            Assert.Equal(1000, device.Settings[30]);

            await WaitUntil(() => device.Status.State == GCode.Grbl.MachineState.Idle);
        }

        // The real firmware ignores the socket as an input. Nothing may rely on it.
        Assert.Empty(fake.SocketInbound);
    }

    [Fact]
    public async Task InterfaceBookkeepingIsNotMistakenForGrblOutput()
    {
        await using var fake = new FakeEsp3d();
        await using var transport = Transport(fake);

        var received = new StringBuilder();
        transport.DataReceived += data => { lock (received) received.Append(Encoding.ASCII.GetString(data.Span)); };

        await transport.ConnectAsync();
        await WaitUntil(() => transport.ClientId is not null);
        await Task.Delay(200);

        string text;
        lock (received) text = received.ToString();
        Assert.DoesNotContain("CURRENT_ID", text);
        Assert.DoesNotContain("ACTIVE_ID", text);
        Assert.DoesNotContain("PING", text);
    }

    [Fact]
    public async Task RealtimeBytesAreSentAsThemselvesNotAsUtf8()
    {
        await using var fake = new FakeEsp3d();
        await using var transport = Transport(fake);
        await transport.ConnectAsync();

        // 0x85 is jog cancel. Escaped as UTF-8 it would arrive as C2 85: two bytes,
        // neither of which is jog cancel.
        await transport.WriteRealtimeAsync(0x85);
        await transport.WriteRealtimeAsync((byte)'!');

        await WaitUntil(() => fake.Commands.Length >= 2);
        Assert.Contains(fake.Commands, c => c.SequenceEqual(new byte[] { 0x85 }));
        Assert.Contains(fake.Commands, c => c.SequenceEqual("!"u8.ToArray()));
        Assert.DoesNotContain(fake.Commands, c => c.SequenceEqual(new byte[] { 0xC2, 0x85 }));
    }

    [Fact]
    public async Task AFailedCommandIsAnErrorNotAnInventedReply()
    {
        await using var fake = new FakeEsp3d();
        await using var transport = Transport(fake);
        await transport.ConnectAsync();

        var received = new StringBuilder();
        transport.DataReceived += data => { lock (received) received.Append(Encoding.ASCII.GetString(data.Span)); };

        fake.StopAnsweringCommands();

        await Assert.ThrowsAsync<IOException>(async () => await transport.WriteAsync("G0 X10\n"u8.ToArray()));
        // And a stop that did not arrive must not look like one that did.
        await Assert.ThrowsAsync<IOException>(async () => await transport.WriteRealtimeAsync(0x18));

        lock (received) Assert.DoesNotContain("error", received.ToString());
    }

    [Fact]
    public async Task AJobIsRefusedBeforeAnyOfItIsSent()
    {
        await using var fake = new FakeEsp3d();
        var device = new GrblDevice(MachineProfile.BlazeXM5Pro()) { StatusPollHz = 10 };
        await using (device)
        {
            await device.ConnectAsync(Transport(fake));
            Assert.False(device.CanStreamJobs);

            var job = new JobDefinition { Name = "square", Lines = ["G1 X10 F1000 S100", "G1 Y10", "G1 X0", "G1 Y0"] };
            var refusal = await Assert.ThrowsAsync<NotSupportedException>(() => device.StartJobAsync(job));

            Assert.Contains("USB", refusal.Message);
            Assert.Equal(JobState.Idle, device.JobState);
            Assert.DoesNotContain(fake.CommandLines, l => job.Lines.Contains(l));
        }
    }

    [Fact]
    public async Task DisconnectingClosesTheSocketProperly()
    {
        await using var fake = new FakeEsp3d();
        var transport = Transport(fake);
        await transport.ConnectAsync();

        await transport.DisconnectAsync();

        // Sessions that simply vanished were part of what hung the real controller's web server.
        await WaitUntil(() => fake.CleanCloseReceived);
    }

    [Fact]
    public async Task ALoginProtectedInterfaceSaysSoInsteadOfFailingVaguely()
    {
        await using var fake = new FakeEsp3d(requireLogin: true);
        await using var transport = Transport(fake);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => transport.ConnectAsync());
        Assert.Contains("login", error.Message);
    }

    [Fact]
    public void TheRecordedReportNamesSocketPort81()
    {
        Assert.Equal(81, Esp3dTransport.ParseWebSocketPort(RecordedReport));
    }

    [Fact]
    public void AnAsyncWebInterfaceIsRefusedRatherThanGuessedAt()
    {
        var asyncReport = RecordedReport.Replace("Sync: 81:192.168.68.91", "Async: 80:192.168.68.91");
        Assert.Throws<NotSupportedException>(() => Esp3dTransport.ParseWebSocketPort(asyncReport));
    }

    [Fact]
    public void TheBlazeXReachesWiFiThroughEsp3d()
    {
        // Built-in fallback and bundled file must agree, or the machine a fresh
        // install shows depends on which one happened to load.
        var builtIn = MachineProfile.BlazeXM5Pro();

        var library = new MachineLibrary();
        library.LoadFolder(Path.Combine(RepoRoot(), "devices"));
        var bundled = library.Find("blazex-m5-pro-10w");

        foreach (var profile in new[] { builtIn, bundled! })
        {
            Assert.Equal(ConnectionKind.Esp3d, profile.NetworkConnection);
            Assert.IsType<Esp3dTransport>(DeviceFactory.CreateTransport(profile, profile.NetworkConnection, "192.168.68.91"));
            // The factory keys the no-DTR-reset serial behaviour on this word; without
            // it, connecting over USB resets the ESP32.
            Assert.Contains("ESP32", profile.Firmware, StringComparison.OrdinalIgnoreCase);
        }

        // A machine that does have a telnet console is unaffected.
        Assert.Equal(ConnectionKind.Tcp, MachineProfile.GenericGrbl().NetworkConnection);
    }

    [Fact]
    public void ThePlainHttpTransportRefusesToStreamAsItAlwaysClaimed()
    {
        // It declared this for months as a static nothing read. Now it is enforced.
        Assert.False(((ITransport)new HttpTransport("127.0.0.1")).SupportsStreaming);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Emberline.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root");
    }
}
