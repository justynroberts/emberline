using Emberline.Core.Machines;
using Emberline.Transport;
using Emberline.VirtualLaser;

namespace Emberline.Devices;

/// <summary>
/// Builds the transport a machine profile asks for.
///
/// Adding a new connection type or a new vendor means registering here, not
/// editing the job engine — which is the extensibility promise in the PRD made
/// concrete rather than aspirational.
/// </summary>
public static class DeviceFactory
{
    /// <summary>
    /// Drivers and transports contributed by plugins.
    ///
    /// A delegate rather than a reference to the plugin assembly, so the device
    /// layer stays unaware that plugins exist at all — which is what keeps the
    /// dependency arrows pointing inward.
    /// </summary>
    public static Func<string, Func<MachineProfile, ILaserDevice>?>? PluginDriverLookup { get; set; }

    public static Func<string, Func<string, ITransport>?>? PluginTransportLookup { get; set; }

    public static ILaserDevice CreateDevice(MachineProfile profile)
    {
        // Built-ins first: a plugin cannot displace the tested GRBL path.
        switch (profile.DriverId)
        {
            case "blazex": return new BlazeXDevice(profile);
            case "grbl": return new GrblDevice(profile);
        }

        var plugin = PluginDriverLookup?.Invoke(profile.DriverId);
        return plugin is not null ? plugin(profile) : new GrblDevice(profile);
    }

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
            {
                // A plugin may have registered this scheme.
                var plugin = PluginTransportLookup?.Invoke(kind.ToString());
                if (plugin is not null && address is not null) return plugin(address);

                throw new NotSupportedException($"Connection type {kind} is not supported.");
            }
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
