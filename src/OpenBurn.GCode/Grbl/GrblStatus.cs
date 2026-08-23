namespace OpenBurn.GCode.Grbl;

public enum MachineState
{
    Disconnected,
    Idle,
    Run,
    Hold,
    Jog,
    Alarm,
    Door,
    Check,
    Home,
    Sleep,
}

public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public override string ToString() => $"{X:0.000}, {Y:0.000}, {Z:0.000}";
}

public readonly record struct BufferState(int PlannerBlocks, int RxBytes);

public readonly record struct Overrides(int Feed, int Rapid, int Spindle)
{
    public static readonly Overrides Default = new(100, 100, 100);
}

/// <summary>One decoded <c>&lt;…&gt;</c> status report.</summary>
public sealed record GrblStatus
{
    public MachineState State { get; init; } = MachineState.Disconnected;

    /// <summary>Sub-state for Hold:0/Hold:1 and Door:0..3, when the controller reports one.</summary>
    public int? SubState { get; init; }

    /// <summary>Machine position. Always populated — derived from WPos + WCO when the controller sends WPos.</summary>
    public Vec3 MachinePosition { get; init; }

    /// <summary>Work position. Always populated — derived from MPos − WCO when the controller sends MPos.</summary>
    public Vec3 WorkPosition { get; init; }

    public Vec3 WorkOffset { get; init; }
    public BufferState? Buffer { get; init; }
    public double Feed { get; init; }
    public double Spindle { get; init; }
    public Overrides Overrides { get; init; } = Overrides.Default;

    /// <summary>Accessory letters, e.g. "SF" for spindle plus flood.</summary>
    public string Accessories { get; init; } = string.Empty;

    /// <summary>Triggered input pins, e.g. "XYP". Empty when nothing is triggered.</summary>
    public string Pins { get; init; } = string.Empty;

    public int? LineNumber { get; init; }

    public bool IsMoving => State is MachineState.Run or MachineState.Jog or MachineState.Home;
    public bool IsFault => State is MachineState.Alarm or MachineState.Door;
    public bool CanStartJob => State is MachineState.Idle or MachineState.Check;

    public static readonly GrblStatus Disconnected = new();
}
