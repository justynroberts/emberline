using System.IO.Ports;

namespace Emberline.Transport;

/// <summary>
/// USB serial. Still the most reliable way to drive a GRBL controller, and the
/// only one guaranteed to exist on every machine Emberline supports.
/// </summary>
public sealed class SerialTransport : TransportBase
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly bool _resetOnConnect;
    private SerialPort? _port;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <param name="resetOnConnect">
    /// Toggling DTR resets an Arduino-based board, which gives a clean welcome
    /// banner but costs about two seconds. ESP32 boards generally do not need it,
    /// and on some of them it drops the Wi-Fi stack.
    /// </param>
    public SerialTransport(string portName, int baudRate = 115200, bool resetOnConnect = true)
    {
        _portName = portName;
        _baudRate = baudRate;
        _resetOnConnect = resetOnConnect;
    }

    public override TransportKind Kind => TransportKind.Serial;
    public override string Description => $"{_portName} @ {_baudRate}";
    public override bool IsConnected => _port?.IsOpen == true;

    public override async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 500,
            WriteTimeout = 2000,
            DtrEnable = false,
            RtsEnable = false,
            NewLine = "\n",
        };

        port.DataReceived += OnDataReceived;
        port.ErrorReceived += OnErrorReceived;
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();

        if (_resetOnConnect)
        {
            // The classic Arduino auto-reset: pulse DTR, then wait for the
            // bootloader to hand over to the firmware.
            port.DtrEnable = true;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            port.DtrEnable = false;
            await Task.Delay(1800, cancellationToken).ConfigureAwait(false);
        }

        _port = port;
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        var port = _port;
        if (port is null || !port.IsOpen) return;

        try
        {
            var available = port.BytesToRead;
            if (available <= 0) return;
            var buffer = new byte[available];
            var read = port.Read(buffer, 0, available);
            if (read > 0) RaiseData(buffer.AsMemory(0, read).ToArray());
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            RaiseDisconnected(ex);
        }
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e) =>
        RaiseDisconnected(new IOException($"Serial error: {e.EventType}"));

    public override Task DisconnectAsync()
    {
        if (_port is { } port)
        {
            port.DataReceived -= OnDataReceived;
            port.ErrorReceived -= OnErrorReceived;
            try
            {
                if (port.IsOpen) port.Close();
            }
            catch
            {
                // A device unplugged mid-session throws here; nothing useful to do.
            }
            port.Dispose();
            _port = null;
        }
        return Task.CompletedTask;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var port = _port ?? throw new InvalidOperationException("Not connected.");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await port.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

public sealed record SerialPortInfo(string PortName, string Description, bool LikelyController);

/// <summary>Serial port enumeration with a guess at which ports are worth trying.</summary>
public static class SerialPortEnumerator
{
    /// <summary>
    /// USB-serial bridges that show up on laser controllers. Matching on the device
    /// node name is crude but works on all three platforms without a native
    /// dependency, and the user can always pick a port manually.
    /// </summary>
    private static readonly string[] LikelyFragments =
    [
        "usbserial", "usbmodem", "wchusbserial", "SLAB_USBtoUART", "cu.usb",
        "ttyUSB", "ttyACM", "COM",
    ];

    public static IReadOnlyList<SerialPortInfo> List()
    {
        string[] names;
        try
        {
            names = SerialPort.GetPortNames();
        }
        catch (PlatformNotSupportedException)
        {
            return [];
        }

        var result = new List<SerialPortInfo>();
        foreach (var name in names.Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            // macOS exposes both /dev/tty.* and /dev/cu.* for one device. The tty
            // node blocks on open waiting for carrier detect, which hangs forever
            // on a USB adapter — always use cu.
            if (name.StartsWith("/dev/tty.", StringComparison.Ordinal)) continue;

            var likely = LikelyFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));
            result.Add(new SerialPortInfo(name, DescribeFor(name), likely));
        }
        return result;
    }

    private static string DescribeFor(string name)
    {
        if (name.Contains("wchusbserial", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("wch", StringComparison.OrdinalIgnoreCase))
        {
            return "CH340/CH341 USB serial — common on GRBL boards";
        }
        if (name.Contains("SLAB", StringComparison.OrdinalIgnoreCase)) return "Silicon Labs CP210x USB serial";
        if (name.Contains("usbmodem", StringComparison.OrdinalIgnoreCase)) return "USB CDC device";
        if (name.Contains("usbserial", StringComparison.OrdinalIgnoreCase)) return "USB serial adapter";
        if (name.Contains("ttyACM", StringComparison.Ordinal)) return "USB CDC device";
        if (name.Contains("ttyUSB", StringComparison.Ordinal)) return "USB serial adapter";
        return "Serial port";
    }
}
