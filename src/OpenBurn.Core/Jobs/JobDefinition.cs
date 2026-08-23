using OpenBurn.Core.Geometry;

namespace OpenBurn.Core.Jobs;

/// <summary>
/// A prepared job: the exact lines that will be sent, plus everything needed to
/// describe, validate, resume and later reproduce it.
/// </summary>
public sealed record JobDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>Bounds of the burning moves, used for framing and bed checks.</summary>
    public Rect2 Bounds { get; init; } = Rect2.Empty;

    public TimeSpan EstimatedDuration { get; init; }

    /// <summary>Machine profile id this was generated for. Running it elsewhere needs a warning.</summary>
    public string? MachineProfileId { get; init; }

    /// <summary>Material preset used, for the job library.</summary>
    public string? MaterialName { get; init; }

    /// <summary>Source artwork paths, for reproducing the job later.</summary>
    public IReadOnlyList<string> SourceFiles { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Resume from this line instead of the beginning.</summary>
    public int StartLine { get; init; }

    public int LineCount => Lines.Count;
}

/// <summary>What a framing pass should trace.</summary>
public enum FramingMode
{
    /// <summary>The axis-aligned bounding box — fast, and what most people expect.</summary>
    Rectangle,

    /// <summary>Convex hull, which hugs rotated or diagonal artwork much more closely.</summary>
    Hull,

    /// <summary>The actual outlines. Slow, but exact.</summary>
    Outline,
}

public sealed record FramingOptions
{
    public FramingMode Mode { get; init; } = FramingMode.Rectangle;

    /// <summary>mm/min.</summary>
    public double FeedRate { get; init; } = 3000;

    /// <summary>
    /// Power as a percentage. A visible-but-harmless pointer level, typically well
    /// under 1 % on a diode laser. Zero means move with the beam off.
    /// </summary>
    public double PowerPercent { get; init; } = 0.5;

    /// <summary>Repeat the frame so the operator can watch it more than once.</summary>
    public int Repeats { get; init; } = 1;

    /// <summary>Extra margin around the artwork, millimetres.</summary>
    public double MarginMm { get; init; }
}
