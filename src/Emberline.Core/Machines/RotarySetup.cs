namespace Emberline.Core.Machines;

public enum RotaryKind
{
    /// <summary>Two rollers turn the workpiece by friction. The roller's diameter sets the ratio.</summary>
    Roller,

    /// <summary>A chuck turns the workpiece directly. The workpiece's own diameter sets the ratio.</summary>
    Chuck,
}

/// <summary>
/// A rotary attachment.
///
/// When a rotary is fitted, the machine's rotary axis no longer moves the gantry
/// in a straight line — it spins something. The controller's steps-per-millimetre
/// for that axis therefore no longer means millimetres of travel, and a job sent
/// unmodified engraves at completely the wrong scale around the workpiece.
///
/// The conversion is the same for both kinds, with a different diameter:
///
///   commanded mm = surface mm × stepsPerRotation ÷ (axisStepsPerMm × π × diameter)
///
/// For a chuck the diameter is the workpiece's, because the chuck turns it
/// directly. For rollers it is the *roller's*, because the roller surface and the
/// workpiece surface move together — the workpiece diameter cancels out, which is
/// the part people get wrong.
/// </summary>
public sealed record RotarySetup
{
    public bool Enabled { get; init; }

    public RotaryKind Kind { get; init; } = RotaryKind.Roller;

    /// <summary>Diameter of the workpiece being engraved, millimetres.</summary>
    public double WorkpieceDiameterMm { get; init; } = 60;

    /// <summary>Diameter of the drive rollers, millimetres. Only used in roller mode.</summary>
    public double RollerDiameterMm { get; init; } = 20;

    /// <summary>Motor steps for one full rotation of the rotary axis.</summary>
    public double StepsPerRotation { get; init; } = 6400;

    /// <summary>
    /// The controller's steps-per-millimetre for the axis the rotary replaces,
    /// normally Y — GRBL's $101. Read from the machine rather than guessed.
    /// </summary>
    public double AxisStepsPerMm { get; init; } = 80;

    /// <summary>Which axis the rotary drives. Y on almost every diode laser.</summary>
    public char Axis { get; init; } = 'Y';

    public static readonly RotarySetup Disabled = new();

    /// <summary>The diameter that actually sets the ratio.</summary>
    public double EffectiveDiameterMm => Kind == RotaryKind.Chuck ? WorkpieceDiameterMm : RollerDiameterMm;

    public bool IsUsable =>
        Enabled &&
        EffectiveDiameterMm > 0.1 &&
        StepsPerRotation > 1 &&
        AxisStepsPerMm > 0.001;

    /// <summary>
    /// Multiply a surface millimetre by this to get the millimetre to command.
    /// One means no change, which is what a disabled or degenerate setup gives.
    /// </summary>
    public double ScaleFactor
    {
        get
        {
            if (!IsUsable) return 1;
            return StepsPerRotation / (AxisStepsPerMm * Math.PI * EffectiveDiameterMm);
        }
    }

    /// <summary>How far around the workpiece one full turn takes you, millimetres.</summary>
    public double CircumferenceMm => Math.PI * Math.Max(0, WorkpieceDiameterMm);

    /// <summary>
    /// A sentence the operator can check against their machine, because a rotary
    /// that is out by a factor of three looks fine right up until it is engraved.
    /// </summary>
    public string Describe()
    {
        if (!Enabled) return "Rotary is off. Jobs run on the flat bed.";
        if (!IsUsable) return "Rotary settings are incomplete — check the diameter and steps per rotation.";

        var kind = Kind == RotaryKind.Chuck ? "chuck" : "rollers";
        return $"Rotary on {Axis} via {kind}. " +
               $"Workpiece {WorkpieceDiameterMm:0.#} mm across, {CircumferenceMm:0.#} mm around. " +
               $"Surface millimetres are commanded at ×{ScaleFactor:0.####}.";
    }

    /// <summary>Warnings worth reading before a rotary job runs.</summary>
    public IReadOnlyList<string> Check(double designHeightMm)
    {
        var warnings = new List<string>();
        if (!Enabled) return warnings;

        if (!IsUsable)
        {
            warnings.Add("The rotary setup is incomplete, so the job would be engraved at the wrong scale.");
            return warnings;
        }

        if (designHeightMm > CircumferenceMm + 0.01)
        {
            warnings.Add($"The artwork is {designHeightMm:0.#} mm tall but the workpiece is only " +
                         $"{CircumferenceMm:0.#} mm around. It will overlap itself.");
        }

        if (Kind == RotaryKind.Roller && RollerDiameterMm > WorkpieceDiameterMm)
        {
            warnings.Add("The rollers are larger than the workpiece. Check the measurements — " +
                         "a workpiece smaller than the rollers will not sit on them properly.");
        }

        if (Math.Abs(ScaleFactor - 1) < 0.01)
        {
            warnings.Add("The rotary scale works out at almost exactly 1, which usually means the " +
                         "steps per rotation or the axis steps per millimetre is wrong. Check both before burning.");
        }

        return warnings;
    }
}
