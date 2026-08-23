using OpenBurn.Core.Machines;
using OpenBurn.Transport;
using OpenBurn.VirtualLaser;

namespace OpenBurn.Devices;

/// <summary>
/// Builds the transport a machine profile asks for.
///
/// Adding a new connection type or a new vendor means registering here, not
/// editing the job engine — which is the extensibility promise in the PRD made
/// concrete rather than aspirational.
/// </summary>
public static class DeviceFactory
{
    public static ILaserDevice CreateDevice(MachineProfile profile) => profile.DriverId switch
    {
        "blazex" => new BlazeXDevice(profile),
        _ => new GrblDevice(profile),
    };

    public static ITransport CreateTransport(MachineProfile profile, ConnectionKind kind, string? address = null)
    {
        switch (kind)
        {
            case ConnectionKind.Serial:
            {
                var port = address ?? throw new ArgumentException("A serial port name is required.", nameof(address));
                // ESP32-based boards generally dislike the DTR reset pulse; AVR boards need it.
                var resetOnConnect = !profile.Firmware.Contains("ESP32", StringComparison.OrdinalIgnoreCase);
                return new SerialTransport(port, profile.BaudRate, resetOnConnect);
            }

            case ConnectionKind.Tcp:
            {
                var (host, port) = SplitAddress(address ?? profile.Host, profile.TcpPort);
                return new TcpTransport(host, port);
            }

            case ConnectionKind.WebSocket:
            {
                var (host, port) = SplitAddress(address ?? profile.Host, profile.WebSocketPort);
                return new WebSocketTransport(host, port);
            }

            case ConnectionKind.Http:
            {
                var (host, port) = SplitAddress(address ?? profile.Host, 80);
                return new HttpTransport(host, port);
            }

            case ConnectionKind.Virtual:
                return new VirtualTransport(new VirtualLaserOptions
                {
                    BedWidthMm = profile.BedWidthMm,
                    BedHeightMm = profile.BedHeightMm,
                    LaserMode = profile.Capabilities.HasFlag(MachineCapabilities.LaserMode),
                    HomingEnabled = profile.Capabilities.HasFlag(MachineCapabilities.Homing),
                    SoftLimitsEnabled = profile.Capabilities.HasFlag(MachineCapabilities.SoftLimits),
                });

            default:
                throw new NotSupportedException($"Connection type {kind} is not supported.");
        }
    }

    private static (string Host, int Port) SplitAddress(string? address, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("A host address is required.", nameof(address));

        var idx = address.LastIndexOf(':');
        if (idx > 0 && int.TryParse(address.AsSpan(idx + 1), out var port)) return (address[..idx], port);
        return (address, defaultPort);
    }
}
