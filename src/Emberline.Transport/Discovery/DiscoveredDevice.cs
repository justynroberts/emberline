namespace Emberline.Transport.Discovery;

public sealed record DiscoveredDevice
{
    public required string Name { get; init; }
    public required TransportKind Transport { get; init; }

    /// <summary>Serial port name, or host:port for a network device.</summary>
    public required string Address { get; init; }

    /// <summary>Firmware banner, if one was read during the probe.</summary>
    public string? Firmware { get; init; }

    /// <summary>What found it — "mDNS", "TCP scan", "USB", "manual".</summary>
    public required string Source { get; init; }

    /// <summary>Confidence that this really is a GRBL controller, 0–1.</summary>
    public double Confidence { get; init; } = 0.5;

    public string? Manufacturer { get; init; }

    public string Summary => Firmware is null
        ? $"{Name} · {Address}"
        : $"{Name} · {Address} · {Firmware}";
}
