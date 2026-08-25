namespace Emberline.VirtualLaser;

/// <summary>Knobs for making the simulator behave like a specific real machine, or misbehave on purpose.</summary>
public sealed record VirtualLaserOptions
{
    public string FirmwareVersion { get; init; } = "1.1h";

    /// <summary>Serial receive buffer size. The whole point of character counting.</summary>
    public int RxBufferSize { get; init; } = 128;

    /// <summary>Motion planner block count. GRBL 1.1 builds with 15 on an Uno, 35 on a Mega.</summary>
    public int PlannerBlocks { get; init; } = 15;

    public double BedWidthMm { get; init; } = 400;
    public double BedHeightMm { get; init; } = 400;

    /// <summary>Starts in Alarm, the way a machine with homing enabled does after power-on.</summary>
    public bool StartInAlarm { get; init; }

    public bool HomingEnabled { get; init; } = true;
    public bool SoftLimitsEnabled { get; init; }
    public bool LaserMode { get; init; } = true;

    /// <summary>Seconds of latency before a response is emitted. Zero keeps tests deterministic and fast.</summary>
    public double ResponseLatencySeconds { get; init; }

    /// <summary>Reject one in N lines with error:20, for exercising error handling.</summary>
    public int FaultEveryNLines { get; init; }

    public static readonly VirtualLaserOptions Default = new();
}
