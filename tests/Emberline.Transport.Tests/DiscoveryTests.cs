using System.Net;
using System.Net.Sockets;
using System.Text;
using Emberline.Transport.Discovery;
using Xunit;

namespace Emberline.Transport.Tests;

/// <summary>
/// What the network probe is allowed to say.
///
/// A scan sweeps every address on the subnet before it knows what any of them
/// are, so whatever the probe sends lands on printers, routers and — the one that
/// matters — lasers that may be part-way through a job. GRBL's 0x18 is a soft
/// reset. Discovery must never be able to stop a machine.
/// </summary>
public class DiscoveryTests
{
    /// <summary>A one-shot TCP server that records what it is sent and answers as GRBL.</summary>
    private sealed class FakeController : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _loop;
        private readonly List<byte> _received = [];
        private readonly object _lock = new();

        public FakeController(string? reply, string? greeting = null)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(async () =>
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();

                    if (greeting is not null)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(greeting));
                    }

                    var buffer = new byte[256];
                    var deadline = DateTime.UtcNow.AddSeconds(3);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (!stream.DataAvailable) { await Task.Delay(20); continue; }
                        var read = await stream.ReadAsync(buffer);
                        if (read <= 0) break;

                        lock (_lock) _received.AddRange(buffer[..read]);

                        if (reply is not null)
                        {
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
                            break;
                        }
                    }
                    await Task.Delay(200);
                }
                catch
                {
                    // The probe closing first is normal.
                }
            });
        }

        public int Port { get; }

        public byte[] Received
        {
            get { lock (_lock) return [.. _received]; }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await _loop; } catch { }
        }
    }

    private static Task<DiscoveredDevice?> Probe(int port) =>
        DeviceDiscovery.ProbeTcpAsync("127.0.0.1", port, TimeSpan.FromSeconds(2));

    [Fact]
    public async Task TheProbeNeverSendsASoftReset()
    {
        await using var fake = new FakeController("[VER:1.1h.20190830:]\nok\n");

        await Probe(fake.Port);

        Assert.DoesNotContain((byte)0x18, fake.Received);
    }

    [Fact]
    public async Task ItSendsNothingButAReadOnlyQuery()
    {
        await using var fake = new FakeController("[VER:1.1h.20190830:]\nok\n");

        await Probe(fake.Port);

        var sent = Encoding.ASCII.GetString(fake.Received).Trim();
        Assert.Equal("$I", sent);
    }

    [Fact]
    public async Task AControllerThatAnswersIsFound()
    {
        await using var fake = new FakeController("[VER:1.1h.20190830:][OPT:VL,15,128]\nok\n");

        var found = await Probe(fake.Port);

        Assert.NotNull(found);
        Assert.Equal($"127.0.0.1:{fake.Port}", found!.Address);
        Assert.Contains("1.1h", found.Firmware ?? "");
    }

    [Fact]
    public async Task AControllerThatAnnouncesOnConnectIsStillFound()
    {
        // ESP32 bridges usually greet the moment the socket opens. Listening
        // before speaking is what replaces the old reset.
        await using var fake = new FakeController(reply: null, greeting: "Grbl 1.1f ['$' for help]\r\nok\r\n");

        var found = await Probe(fake.Port);

        Assert.NotNull(found);
        Assert.DoesNotContain((byte)0x18, fake.Received);
    }

    [Fact]
    public async Task SomethingThatIsNotAControllerIsNotReportedAsOne()
    {
        await using var fake = new FakeController("HTTP/1.1 400 Bad Request\r\n\r\n");

        Assert.Null(await Probe(fake.Port));
        Assert.DoesNotContain((byte)0x18, fake.Received);
    }

    [Fact]
    public async Task AClosedPortIsNotADevice()
    {
        // Port 1 on loopback: nothing listens, so this must fail fast and quietly.
        Assert.Null(await DeviceDiscovery.ProbeTcpAsync("127.0.0.1", 1, TimeSpan.FromMilliseconds(400)));
    }
}
