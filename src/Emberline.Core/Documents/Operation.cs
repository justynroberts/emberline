namespace Emberline.Core.Documents;

/// <summary>What the laser does with the geometry on a layer.</summary>
public enum OperationKind
{
    /// <summary>Follow the outline at engraving power — a visible line, not a cut.</summary>
    Engrave,

    /// <summary>Follow the outline at cutting power, usually with several passes.</summary>
    Cut,

    /// <summary>Raster or hatch the interior of closed shapes.</summary>
    Fill,

    /// <summary>A light outline pass, typically for fold lines or registration.</summary>
    Score,
}

/// <summary>How a Fill operation covers the interior.</summary>
public enum FillStrategy
{
    /// <summary>Scan lines at a fixed angle — the classic raster fill.</summary>
    Hatch,

    /// <summary>Hatch in two directions for a denser, darker result.</summary>
    CrossHatch,

    /// <summary>Concentric inward offsets of the outline.</summary>
    Offset,
}

public static class OperationDefaults
{
    /// <summary>
    /// Starting points when a user adds a layer before choosing a material.
    /// Deliberately conservative — under-burning wastes a minute, over-burning
    /// wastes the workpiece.
    /// </summary>
    public static (double SpeedMmMin, double PowerPercent, int Passes) For(OperationKind kind) => kind switch
    {
        OperationKind.Engrave => (3000, 25, 1),
        OperationKind.Score => (1200, 40, 1),
        OperationKind.Cut => (350, 90, 3),
        OperationKind.Fill => (3000, 30, 1),
        _ => (3000, 25, 1),
    };
}
