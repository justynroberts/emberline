namespace Emberline.GCode.Grbl;

public enum SettingKind { Bool, Int, Float, Mask, Microseconds, Milliseconds, Millimetres, MmPerMin, MmPerSec2, StepsPerMm }

public sealed record GrblSettingDef(
    int Key,
    string Name,
    string Unit,
    SettingKind Kind,
    string Group,
    string Description,
    string[]? Bits = null)
{
    /// <summary>
    /// Set on the settings where a wrong value drives the machine into itself.
    ///
    /// The PRD asks that dangerous configuration commands require confirmation,
    /// and this is the list that deserves it: invert a homing direction and the
    /// next $H drives the gantry into the far end of the rails at seek speed.
    /// </summary>
    public string? DangerNote { get; init; }

    public bool IsDangerous => DangerNote is not null;
}

/// <summary>
/// The <c>$</c> settings registry.
///
/// The controller only ever tells you <c>$110=6000</c>. Everything a human needs
/// to judge whether that number is right — what it is, its unit, whether it is a
/// bitmask, and what breaks if it is wrong — lives here.
/// </summary>
public static class GrblSettings
{
    public const string GroupSpindle = "Spindle & laser";
    public const string GroupMotion = "Motion";
    public const string GroupLimits = "Limits & homing";
    public const string GroupStepper = "Stepper";
    public const string GroupReporting = "Reporting";

    private static readonly string[] AxisBits = ["X", "Y", "Z"];

    public static readonly IReadOnlyList<GrblSettingDef> All = BuildAll();

    public static readonly IReadOnlyDictionary<int, GrblSettingDef> ByKey =
        All.ToDictionary(s => s.Key);

    public static readonly IReadOnlyList<string> Groups =
    [
        GroupSpindle, GroupMotion, GroupLimits, GroupStepper, GroupReporting,
        "Axis: X", "Axis: Y", "Axis: Z",
    ];

    private static List<GrblSettingDef> BuildAll()
    {
        var list = new List<GrblSettingDef>
        {
            new(0, "Step pulse time", "µs", SettingKind.Microseconds, GroupStepper, "Width of the step pulse sent to the drivers. 10 µs suits most boards; the firmware rejects anything below 3."),
            new(1, "Step idle delay", "ms", SettingKind.Milliseconds, GroupStepper, "How long motors stay energised after a move. 255 keeps them locked permanently, which holds position but runs hot."),
            new(2, "Step pulse invert", "mask", SettingKind.Mask, GroupStepper, "Inverts the step signal per axis. Only needed for unusual driver wiring.", AxisBits),
            new(3, "Step direction invert", "mask", SettingKind.Mask, GroupStepper, "Flips travel direction per axis. This is the setting to change when an axis moves the wrong way.", AxisBits)
            {
                DangerNote = "Reversing an axis changes which way the machine moves. If homing is enabled, the next homing cycle will drive that axis away from its switch and into the end of its travel.",
            },
            new(4, "Invert step enable pin", "bool", SettingKind.Bool, GroupStepper, "Inverts the stepper-enable output for drivers expecting active-high."),
            new(5, "Invert limit pins", "bool", SettingKind.Bool, GroupLimits, "Set to 1 for normally-closed limit switches. Getting this wrong is the usual cause of ALARM:8.")
            {
                DangerNote = "With this wrong, the controller believes a switch is pressed when it is not — or misses it entirely and runs the axis into its end stop.",
            },
            new(6, "Invert probe pin", "bool", SettingKind.Bool, GroupLimits, "Inverts the probe input."),
            new(10, "Status report mask", "mask", SettingKind.Mask, GroupReporting, "Which fields appear in the ? status report. Emberline wants position and buffer state, which is $10=3.", ["Machine position", "Buffer state"]),
            new(11, "Junction deviation", "mm", SettingKind.Float, GroupMotion, "How aggressively the planner rounds corners. Higher is faster and less accurate; 0.01 suits lasers."),
            new(12, "Arc tolerance", "mm", SettingKind.Float, GroupMotion, "Chord error when the firmware breaks an arc into segments. 0.002 mm is finer than any laser can resolve."),
            new(13, "Report in inches", "bool", SettingKind.Bool, GroupReporting, "Reports position in inches. Emberline works in millimetres — leave this at 0."),
            new(20, "Soft limits", "bool", SettingKind.Bool, GroupLimits, "Refuses moves that would leave the bed. Requires homing ($22) and correct $130–$132."),
            new(21, "Hard limits", "bool", SettingKind.Bool, GroupLimits, "Raises an immediate alarm when a limit switch closes. Needs switches that are not electrically noisy.")
            {
                DangerNote = "Turning hard limits on with noisy or unwired switches will alarm mid-job. Turning them off removes the machine's last protection against running off its rails.",
            },
            new(22, "Homing cycle", "bool", SettingKind.Bool, GroupLimits, "Enables $H. Without it the machine has no absolute reference and soft limits cannot work."),
            new(23, "Homing direction invert", "mask", SettingKind.Mask, GroupLimits, "Which way each axis travels to find its switch. Most diode lasers home front-left, which needs Y inverted.", AxisBits)
            {
                DangerNote = "Get this wrong and the next homing cycle drives the axis away from its switch, at seek speed, into the end of its travel.",
            },
            new(24, "Homing feed rate", "mm/min", SettingKind.MmPerMin, GroupLimits, "Slow, precise approach speed for the second homing pass."),
            new(25, "Homing seek rate", "mm/min", SettingKind.MmPerMin, GroupLimits, "Fast speed used to find the switch on the first pass.")
            {
                DangerNote = "This is the speed the machine will hit its end stop at if a limit switch fails to trigger.",
            },
            new(26, "Homing debounce", "ms", SettingKind.Milliseconds, GroupLimits, "Settling time after the switch triggers. Raise it if homing is erratic."),
            new(27, "Homing pull-off", "mm", SettingKind.Millimetres, GroupLimits, "Distance backed off the switch after homing, so hard limits do not immediately re-trigger."),
            new(30, "Maximum spindle speed", "S", SettingKind.Int, GroupSpindle, "The S value that means 100 % laser power. Emberline scales every power percentage to this ceiling — if it is wrong, every burn is wrong.")
            {
                DangerNote = "Every power percentage in every job is scaled to this number. Lowering it silently under-burns; raising it silently over-burns.",
            },
            new(31, "Minimum spindle speed", "S", SettingKind.Int, GroupSpindle, "The S value that means minimum output. Leave at 0 for a laser."),
            new(32, "Laser mode", "bool", SettingKind.Bool, GroupSpindle, "Dynamic power (M4): output scales with actual feed rate so corners and accelerations do not over-burn. Essential for photo engraving."),
        };

        (int Base, string Name, SettingKind Kind, string Unit, string Description)[] perAxis =
        [
            (100, "Travel resolution", SettingKind.StepsPerMm, "steps/mm", "Steps required to move this axis one millimetre. Calibrate by commanding a 100 mm move and measuring what you get."),
            (110, "Maximum rate", SettingKind.MmPerMin, "mm/min", "Fastest this axis will move, used for G0 rapids. Set it to the speed at which the axis still moves reliably, minus a margin."),
            (120, "Acceleration", SettingKind.MmPerSec2, "mm/sec²", "How hard this axis accelerates. Too high loses steps on direction changes; too low rounds corners and slows raster work badly."),
            (130, "Maximum travel", SettingKind.Millimetres, "mm", "Length of the axis measured from the homing switch. Only used when soft limits are on."),
        ];

        foreach (var (baseKey, name, kind, unit, description) in perAxis)
        {
            for (var i = 0; i < AxisBits.Length; i++)
            {
                var axis = AxisBits[i];
                list.Add(new GrblSettingDef(baseKey + i, $"{name} ({axis})", unit, kind, $"Axis: {axis}", description)
                {
                    // Steps-per-millimetre decides how far every move actually goes.
                    DangerNote = baseKey == 100
                        ? "Every distance the machine travels is scaled by this. A wrong value makes every job the wrong size, and a very wrong one drives the axis past its limits."
                        : null,
                });
            }
        }

        return list;
    }

    public static GrblSettingDef Describe(int key) => ByKey.TryGetValue(key, out var def)
        ? def
        : new GrblSettingDef(key, $"Setting ${key}", string.Empty, SettingKind.Float, GroupMotion,
            "Not part of the GRBL 1.1 core set — most likely a firmware-specific extension.");

    public static string Format(GrblSettingDef def, double value) => def.Kind switch
    {
        SettingKind.Bool => value != 0 ? "Enabled" : "Disabled",
        SettingKind.Mask when def.Bits is { Length: > 0 } bits =>
            string.Join(", ", bits.Where((_, i) => ((int)value >> i & 1) == 1).DefaultIfEmpty("None")),
        SettingKind.Int => ((int)Math.Round(value)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
    };

    public sealed record SettingWarning(int Key, bool IsError, string Text);

    /// <summary>
    /// The configuration mistakes that actually ruin jobs, checked against a
    /// settings snapshot read from the machine.
    /// </summary>
    public static List<SettingWarning> Audit(IReadOnlyDictionary<int, double> values)
    {
        var warnings = new List<SettingWarning>();

        double? Get(int k) => values.TryGetValue(k, out var v) ? v : null;

        if (Get(20) == 1 && Get(22) == 0)
        {
            warnings.Add(new SettingWarning(20, true,
                "Soft limits ($20) are on but homing ($22) is off. The controller will reject moves with error:10."));
        }

        if (Get(32) == 0)
        {
            warnings.Add(new SettingWarning(32, false,
                "Laser mode ($32) is off. Power will not scale with speed, so corners and line starts will over-burn. Turn it on unless you are driving a spindle."));
        }

        if (Get(30) is { } maxS && Math.Abs(maxS - 1000) > 0.5)
        {
            warnings.Add(new SettingWarning(30, false,
                $"Maximum spindle speed ($30) is {maxS:0}. Emberline scales power to this value — make sure the machine profile uses the same number."));
        }

        if (Get(10) is { } report && ((int)report & 2) == 0)
        {
            warnings.Add(new SettingWarning(10, false,
                "Status reports ($10) do not include buffer state. Streaming still works, but the buffer gauge will be blank. Set $10=3."));
        }

        if (Get(11) is { } jd && jd > 0.05)
        {
            warnings.Add(new SettingWarning(11, false,
                $"Junction deviation ($11) is {jd:0.###} mm. Above about 0.05 mm, small detail visibly rounds off."));
        }

        if (Get(13) == 1)
        {
            warnings.Add(new SettingWarning(13, true,
                "The controller is reporting in inches ($13=1). Emberline expects millimetres — set $13=0."));
        }

        return warnings;
    }
}
