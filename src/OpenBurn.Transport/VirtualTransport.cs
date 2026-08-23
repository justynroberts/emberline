using System.Text;
using OpenBurn.VirtualLaser;

namespace OpenBurn.Transport;

/// <summary>
/// The in-process simulator dressed as a transport, so the whole application —
/// job engine, console, jog pad, status panel — can be driven with no hardware
/// attached. It is selectable at runtime, not just in tests: it is how someone
/// evaluates OpenBurn before their machine arrives.
/// </summary>
public sealed class VirtualTransport : TransportBase
{
    private readonly VirtualGrblController _controller;
    private readonly double _realTimeScale;
    private CancellationTokenSource? _ticker;

    public VirtualTransport(VirtualLaserOptions? options = null, double realTimeScale = 1.0)
    {
        _controller = new VirtualGrblController(options);
        _realTimeScale = realTimeScale <= 0 ? 1.0 : realTimeScale;
        _controller.LineEmitted += OnLine;
    }

    /// <summary>Direct access for tests and for the diagnostics panel.</summary>
    public VirtualGrblController Controller => _controller;

    public override TransportKind Kind => TransportKind.Virtual;
    public override string Description => "Virtual laser (in-process simulator)";
    public override bool IsConnected { get; } = true;

    private void OnLine(string line) => RaiseData(Encoding.ASCII.GetBytes(line + "\r\n"));

    public override Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _ticker?.Cancel();
        _ticker = new CancellationTokenSource();
        var token = _ticker.Token;

        _ = Task.Run(async () =>
        {
            // A real machine's motion advances whether or not anyone is looking, so
            // the simulator needs its own clock once it is acting as a transport.
            var last = DateTimeOffset.UtcNow;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(10, token).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                var delta = (now - last).TotalSeconds * _realTimeScale;
                last = now;
                lock (_controller) _controller.Tick(delta);
            }
        }, CancellationToken.None);

        // Re-announce so the connecting device sees a welcome banner, as it would
        // from a freshly reset board.
        lock (_controller) _controller.Write("\x18");
        return Task.CompletedTask;
    }

    public override Task DisconnectAsync()
    {
        _ticker?.Cancel();
        _ticker?.Dispose();
        _ticker = null;
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        lock (_controller) _controller.Write(data.Span);
        return ValueTask.CompletedTask;
    }
}
