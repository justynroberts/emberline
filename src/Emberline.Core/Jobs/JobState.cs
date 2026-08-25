namespace Emberline.Core.Jobs;

/// <summary>Lifecycle of one job, per the job-engine section of the PRD.</summary>
public enum JobState
{
    /// <summary>Nothing loaded.</summary>
    Idle,

    /// <summary>Accepted and waiting for the machine to be ready.</summary>
    Queued,

    /// <summary>Generating and validating G-code.</summary>
    Preparing,

    Running,
    Paused,
    Completed,
    Cancelled,

    /// <summary>Stopped by an error, an alarm, or the link dropping.</summary>
    Failed,
}

public static class JobStateExtensions
{
    public static bool IsTerminal(this JobState state) =>
        state is JobState.Completed or JobState.Cancelled or JobState.Failed;

    public static bool IsActive(this JobState state) =>
        state is JobState.Preparing or JobState.Running or JobState.Paused;
}

public readonly record struct JobProgress(
    JobState State,
    int LinesSent,
    int LinesAcknowledged,
    int TotalLines,
    TimeSpan Elapsed,
    TimeSpan EstimatedRemaining,
    string CurrentLine)
{
    public double Fraction => TotalLines > 0 ? (double)LinesAcknowledged / TotalLines : 0;
    public double Percent => Fraction * 100;

    public static readonly JobProgress Empty =
        new(JobState.Idle, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero, string.Empty);
}
