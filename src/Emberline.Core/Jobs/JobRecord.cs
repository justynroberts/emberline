namespace Emberline.Core.Jobs;

/// <summary>
/// One completed job, kept so it can be found and reproduced later. This is the
/// job library from the PRD: what was burned, on what, with which settings.
/// </summary>
public sealed record JobRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public required JobState Outcome { get; init; }

    public string? MachineName { get; init; }
    public string? MaterialName { get; init; }

    public double SpeedMmMin { get; init; }
    public double PowerPercent { get; init; }
    public int Passes { get; init; }

    public int TotalLines { get; init; }
    public int LinesCompleted { get; init; }

    public double WidthMm { get; init; }
    public double HeightMm { get; init; }

    /// <summary>Source artwork, so "run it again" can re-open the right files.</summary>
    public IReadOnlyList<string> SourceFiles { get; init; } = [];

    /// <summary>Relative path to a PNG thumbnail in the job library folder.</summary>
    public string? ThumbnailPath { get; init; }

    /// <summary>Relative path to the archived G-code, when the user asked to keep it.</summary>
    public string? GcodePath { get; init; }

    public string? FailureReason { get; init; }

    public TimeSpan Duration => (FinishedAt ?? StartedAt) - StartedAt;

    public string Summary =>
        $"{Name} · {MaterialName ?? "no material"} · {SpeedMmMin:0} mm/min @ {PowerPercent:0}% × {Passes}";
}
