using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace OpenBurn.Transport.Discovery;

/// <summary>
/// Finds machines without the user typing an IP address.
///
/// Three strategies, cheapest first: enumerate USB serial ports, listen for mDNS
/// announcements, and — only when the user explicitly asks, per the PRD — probe
/// the local subnet. The subnet scan is opt-in because unsolicited port scanning
/// on somebody's network is rude and, on a corporate LAN, alarming.
/// </summary>
public sealed class DeviceDiscovery
{
    /// <summary>Ports a GRBL-over-network controller is likely to answer on.</summary>
    public static readonly int[] CommonPorts = [23, 8080, 80, 81];

    public IReadOnlyList<DiscoveredDevice> FindSerialDevices()
    {
        var results = new List<DiscoveredDevice>();
        foreach (var port in SerialPortEnumerator.List())
        {
            results.Add(new DiscoveredDevice
            {
                Name = port.LikelyController ? "GRBL controller (probable)" : "Serial port",
                Transport = TransportKind.Serial,
                Address = port.PortName,
                Source = "USB",
                Confidence = port.LikelyController ? 0.7 : 0.2,
                Firmware = port.Description,
            });
        }
        return results;
    }

    /// <summary>
    /// Probe one endpoint by opening a socket and asking for build info. A GRBL
    /// controller answers with a welcome banner or a bracketed VER line.
    ///
    /// The probe asks and never resets. Sending 0x18 makes a quiet controller
    /// re-announce itself, which is genuinely the most reliable way to identify
    /// one — and it is a soft reset. A scan sweeps every address on the subnet
    /// before it knows what any of them are, so that byte would land on whatever
    /// else answers on these ports, and on a laser part-way through a job it ends
    /// the job. Discovery must not be able to stop a machine. Missing a
    /// controller that only speaks when reset is the better failure.
    /// </summary>
    public static async Task<DiscoveredDevice?> ProbeTcpAsync(
        string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient { NoDelay = true };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();

            // Many bridges announce on connect, so listen briefly before saying
            // anything, then ask for build info. $I is a read-only query.
            await Task.Delay(150, cts.Token).ConfigureAwait(false);
            await stream.WriteAsync("\n$I\n"u8.ToArray(), cts.Token).ConfigureAwait(false);

            var buffer = new byte[1024];
            var text = new StringBuilder();
            var deadline = DateTimeOffset.UtcNow + timeout;

            while (DateTimeOffset.UtcNow < deadline)
            {
                if (!stream.DataAvailable)
                {
                    await Task.Delay(40, cts.Token).ConfigureAwait(false);
                    continue;
                }
                var read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                if (read <= 0) break;
                text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (text.ToString().Contains("ok", StringComparison.Ordinal)) break;
            }

            var banner = text.ToString();
            if (banner.Length == 0) return null;

            var isGrbl = banner.Contains("Grbl", StringComparison.OrdinalIgnoreCase) ||
                         banner.Contains("[VER:", StringComparison.Ordinal) ||
                         banner.Contains("FluidNC", StringComparison.OrdinalIgnoreCase);
            if (!isGrbl) return null;

            return new DiscoveredDevice
            {
                Name = DeriveName(banner),
                Transport = TransportKind.Tcp,
                Address = $"{host}:{port}",
                Firmware = ExtractFirmware(banner),
                Source = "TCP scan",
                Confidence = 0.95,
            };
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sweep the local /24 for controllers. Opt-in, bounded in concurrency so it
    /// does not saturate a home router, and cancellable.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredDevice>> ScanSubnetAsync(
        IProgress<double>? progress = null,
        int maxConcurrency = 48,
        CancellationToken cancellationToken = default)
    {
        var prefixes = LocalIPv4Prefixes();
        var found = new List<DiscoveredDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gate = new SemaphoreSlim(maxConcurrency);
        var completed = 0;
        var total = prefixes.Count * 254;
        if (total == 0) return found;

        var tasks = new List<Task>();
        foreach (var prefix in prefixes)
        {
            for (var host = 1; host <= 254; host++)
            {
                var address = $"{prefix}.{host}";
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        foreach (var port in CommonPorts)
                        {
                            var device = await ProbeTcpAsync(address, port, TimeSpan.FromMilliseconds(600), cancellationToken)
                                .ConfigureAwait(false);
                            if (device is null) continue;
                            lock (found)
                            {
                                if (seen.Add(device.Address)) found.Add(device);
                            }
                            break;
                        }
                    }
                    finally
                    {
                        gate.Release();
                        var done = Interlocked.Increment(ref completed);
                        progress?.Report((double)done / total);
                    }
                }, cancellationToken));
            }
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A cancelled scan still returns whatever it found.
        }

        return found;
    }

    /// <summary>The /24 prefixes of every up, non-loopback IPv4 interface.</summary>
    public static IReadOnlyList<string> LocalIPv4Prefixes()
    {
        var prefixes = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(addr.Address)) continue;

                var bytes = addr.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue; // link-local

                var prefix = $"{bytes[0]}.{bytes[1]}.{bytes[2]}";
                if (!prefixes.Contains(prefix)) prefixes.Add(prefix);
            }
        }
        return prefixes;
    }

    private static string DeriveName(string banner)
    {
        if (banner.Contains("FluidNC", StringComparison.OrdinalIgnoreCase)) return "FluidNC controller";
        if (banner.Contains("BlazeX", StringComparison.OrdinalIgnoreCase)) return "BlazeX laser";
        if (banner.Contains("ESP32", StringComparison.OrdinalIgnoreCase)) return "Grbl_ESP32 controller";
        return "GRBL laser";
    }

    private static string? ExtractFirmware(string banner)
    {
        foreach (var line in banner.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (t.StartsWith("Grbl ", StringComparison.Ordinal)) return t;
            if (t.StartsWith("[VER:", StringComparison.Ordinal)) return t.Trim('[', ']');
        }
        return null;
    }
}
