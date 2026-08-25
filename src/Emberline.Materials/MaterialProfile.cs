using System.Text.Json.Serialization;
using Emberline.Core.Documents;

namespace Emberline.Materials;

/// <summary>One operation's settings for one material at one laser power.</summary>
public sealed record MaterialOperation
{
    public required OperationKind Operation { get; init; }

    /// <summary>mm/min.</summary>
    public required double SpeedMmMin { get; init; }

    /// <summary>0–100.</summary>
    public required double PowerPercent { get; init; }

    public int Passes { get; init; } = 1;

    /// <summary>Scan-line spacing for fills and raster, millimetres.</summary>
    public double LineIntervalMm { get; init; } = 0.1;

    public bool AirAssist { get; init; }

    public string? Notes { get; init; }

    public string Summary => $"{SpeedMmMin:0} mm/min · {PowerPercent:0.#}% · ×{Passes}";
}

/// <summary>
/// A material at a thickness, with the settings that work on it.
///
/// Settings are always tied to a laser power band, because "25 % on plywood" means
/// something completely different on a 5 W diode and a 40 W CO₂. Every profile also
/// carries the wattage it was measured at, so the library can say so rather than
/// silently handing over numbers from the wrong machine.
/// </summary>
public sealed record MaterialProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Top-level group: Wood, Acrylic, Metal, Slate, Paper, Leather, Fabric.</summary>
    public required string Category { get; init; }

    /// <summary>Specific material: Plywood, Basswood, Anodised aluminium.</summary>
    public required string Name { get; init; }

    /// <summary>Millimetres. Zero for surface-marking materials where it does not apply.</summary>
    public double ThicknessMm { get; init; }

    /// <summary>Optical watts these settings were measured at.</summary>
    public required double LaserWatts { get; init; }

    public required IReadOnlyList<MaterialOperation> Operations { get; init; }

    public string? Notes { get; init; }

    /// <summary>False for anything the user added or edited, so the UI can offer a reset.</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>
    /// Materials that release chlorine, cyanide or similar when lasered. Shown as a
    /// warning, never quietly filtered out — the operator decides, but they should
    /// be told.
    /// </summary>
    public string? Hazard { get; init; }

    [JsonIgnore]
    public string DisplayName => ThicknessMm > 0 ? $"{Name} {ThicknessMm:0.#} mm" : Name;

    [JsonIgnore]
    public string FullPath => $"{Category} / {DisplayName}";

    public MaterialOperation? For(OperationKind kind) => Operations.FirstOrDefault(o => o.Operation == kind);

    /// <summary>
    /// Rescale settings measured at one wattage to another.
    ///
    /// Power scales roughly inversely with wattage for the same delivered energy, so
    /// a 25 % setting on a 10 W machine is about 50 % on a 5 W one. This is an
    /// approximation and the UI says so — the honest answer is always to burn a test
    /// grid, which Emberline will generate for you.
    /// </summary>
    public MaterialProfile ScaleTo(double targetWatts)
    {
        if (targetWatts <= 0 || Math.Abs(targetWatts - LaserWatts) < 0.01) return this;

        var ratio = LaserWatts / targetWatts;
        return this with
        {
            LaserWatts = targetWatts,
            IsBuiltIn = false,
            Operations = [.. Operations.Select(op => op with
            {
                PowerPercent = Math.Clamp(op.PowerPercent * ratio, 1, 100),
                // Once power saturates, the only remaining lever is more passes.
                Passes = op.PowerPercent * ratio > 100 ? (int)Math.Ceiling(op.Passes * op.PowerPercent * ratio / 100) : op.Passes,
            })],
            Notes = (Notes is null ? string.Empty : Notes + " ") +
                    $"Scaled from {LaserWatts:0.#} W settings — confirm with a test grid before committing a workpiece.",
        };
    }
}
