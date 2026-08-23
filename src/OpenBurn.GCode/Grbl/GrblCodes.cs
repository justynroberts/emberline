namespace OpenBurn.GCode.Grbl;

/// <summary>A decoded controller code with a remedy, not just a number.</summary>
public sealed record GrblCodeInfo(int Code, string Title, string Message, string Remedy);

/// <summary>The GRBL 1.1 error and alarm tables, written as operator-facing sentences.</summary>
public static class GrblCodes
{
    public static readonly IReadOnlyDictionary<int, GrblCodeInfo> Errors = new Dictionary<int, GrblCodeInfo>
    {
        [1] = new(1, "Bad command letter", "A G-code word used a letter the controller does not recognise.", "Check the line for a stray character or a mangled word."),
        [2] = new(2, "Bad number format", "A numeric value was missing or malformed after a G-code word.", "Look for a word such as X with no number after it."),
        [3] = new(3, "Invalid $ command", "The $ system command was not recognised or is unsupported.", "Send $ for the command list and check your firmware version supports it."),
        [4] = new(4, "Negative value", "A positive value was required but a negative one was given.", "Correct the value — most $ settings reject negatives."),
        [5] = new(5, "Homing disabled", "Homing was commanded but $22 homing is disabled.", "Set $22=1 and configure limit switches, or stop using $H."),
        [6] = new(6, "Step pulse too short", "Minimum step pulse time must be greater than 3 microseconds.", "Raise $0 to 3 or more."),
        [7] = new(7, "EEPROM read failed", "EEPROM read failed and default values were restored.", "Re-write your $ settings and power-cycle the controller."),
        [8] = new(8, "Not idle", "This $ command only runs while the controller is idle.", "Wait for the job to finish, or stop it first."),
        [9] = new(9, "G-code locked out", "G-code is locked out during an alarm or jog state.", "Send $X to unlock, or home the machine with $H."),
        [10] = new(10, "Soft limits need homing", "Soft limits ($20) cannot be enabled without homing ($22).", "Enable $22=1 first, or set $20=0."),
        [11] = new(11, "Line overflow", "The line exceeded the controller's 80-character receive buffer.", "Shorten the line. OpenBurn normally prevents this — please report it."),
        [12] = new(12, "Step rate too high", "The requested step rate exceeds what the controller can generate.", "Lower the $110–$112 maximum rates, or reduce the feed rate."),
        [13] = new(13, "Safety door open", "A safety door was detected as open and door state was entered.", "Close the lid interlock, then resume."),
        [14] = new(14, "Build info too long", "The startup line exceeded the EEPROM line length limit.", "Shorten the $N startup line."),
        [15] = new(15, "Jog target exceeds travel", "The jog target is outside the machine travel envelope.", "Jog a smaller distance, or re-zero the work origin."),
        [16] = new(16, "Invalid jog command", "The jog command is missing its $J= prefix or contains a disallowed word.", "Internal error — please report the console line."),
        [17] = new(17, "Laser mode needs PWM", "Laser mode requires a PWM-capable spindle output.", "Set $32=0, or use a controller with spindle PWM."),
        [20] = new(20, "Unsupported command", "An unsupported or invalid G-code command was found in the block.", "Check for post-processor output your firmware does not implement."),
        [21] = new(21, "Modal group violation", "Two commands from the same modal group were in one block.", "Split the conflicting words onto separate lines."),
        [22] = new(22, "Undefined feed rate", "A feed-rate move was commanded with no feed rate set.", "Add an F word before the first G1/G2/G3 move."),
        [23] = new(23, "Non-integer value", "A command requiring an integer was given a fractional value.", "Check for something like G1.5 or M3.2 in the file."),
        [24] = new(24, "Axis word conflict", "Two commands that both require axis words were in the same block.", "Split the block."),
        [25] = new(25, "Repeated word", "A G-code word was repeated in the block.", "Remove the duplicate word."),
        [26] = new(26, "Missing axis words", "A command that requires axis words was given none.", "Add the required X/Y/Z words."),
        [27] = new(27, "Invalid line number", "The N line number is outside the range 1 to 9,999,999.", "Strip or renumber the N words."),
        [28] = new(28, "Missing required value", "A command was sent without its required P or L value.", "Check G10/G28.1/G30.1-style commands for missing parameters."),
        [29] = new(29, "Unsupported work offset", "Only work coordinate systems G54 to G59 are supported.", "Use G54 through G59 only."),
        [30] = new(30, "G53 needs G0 or G1", "G53 requires either G0 or G1 to be active.", "Write G53 G0 X… instead of G53 X…."),
        [31] = new(31, "Extra axis words", "Axis words were found in a block that does not use them.", "Remove the stray X/Y/Z words."),
        [32] = new(32, "Arc needs two axes", "A G2/G3 arc had no axis words in the selected plane.", "Check the plane (G17/G18/G19) and the axis words."),
        [33] = new(33, "Invalid motion target", "The motion target is invalid — usually an arc whose centre cannot be resolved.", "Regenerate the arc, or convert arcs to line segments on export."),
        [34] = new(34, "Arc radius error", "The arc radius is mathematically impossible for the given endpoints.", "Use I/J centre-format arcs instead of R-format."),
        [35] = new(35, "Arc offsets missing", "No I, J or K offset was given for an arc in the selected plane.", "Add the correct offsets for the active plane."),
        [36] = new(36, "Unused value words", "There are unused value words left in the block.", "Clean up the line."),
        [37] = new(37, "G43.1 axis error", "The dynamic tool length offset is not on the configured tool length axis.", "Apply the offset on the Z axis."),
        [38] = new(38, "Tool number too large", "The tool number exceeds the maximum supported value.", "Use a lower tool number."),
    };

    public static readonly IReadOnlyDictionary<int, GrblCodeInfo> Alarms = new Dictionary<int, GrblCodeInfo>
    {
        [1] = new(1, "Hard limit triggered", "A hard limit switch was hit. Machine position is lost — it may have been moving at speed.", "Send $X to unlock, move away from the switch, then re-home with $H."),
        [2] = new(2, "Soft limit — target out of bounds", "The requested move would leave the machine envelope. Nothing moved.", "Move the job inside the bed, or re-zero the work origin closer to the front-left."),
        [3] = new(3, "Reset while in motion", "The controller was reset while moving, so position is lost.", "Re-home with $H before running another job."),
        [4] = new(4, "Probe fail", "The probe was not in its expected initial state before the G38.2 move.", "Check probe wiring and starting position."),
        [5] = new(5, "Probe fail — no contact", "The probe did not make contact within the programmed travel.", "Start closer to the surface, or increase the travel."),
        [6] = new(6, "Homing fail — reset", "The homing cycle was interrupted by a reset.", "Re-run $H without interrupting it."),
        [7] = new(7, "Homing fail — door opened", "The safety door was opened during homing.", "Close the lid interlock and re-home."),
        [8] = new(8, "Homing fail — switch not found", "The limit switch was not reached. Usually wiring or switch polarity.", "Check $5 limit pin inversion, switch wiring, and that $27 pull-off is smaller than the switch travel."),
        [9] = new(9, "Homing fail — could not clear switch", "The limit switch stayed engaged after the pull-off move.", "Check for a stuck or mis-wired switch; increase the $27 pull-off distance."),
        [10] = new(10, "Homing fail — dual axis", "The self-squaring dual-motor axis did not find its second switch.", "Check the second limit switch on the squared axis."),
    };

    public static GrblCodeInfo DescribeError(int code) => Errors.TryGetValue(code, out var info)
        ? info
        : new GrblCodeInfo(code, $"Unknown error {code}",
            $"The controller reported error:{code}, which is not in the GRBL 1.1 table.",
            "Check your firmware documentation — this may be a vendor-specific code.");

    public static GrblCodeInfo DescribeAlarm(int code) => Alarms.TryGetValue(code, out var info)
        ? info
        : new GrblCodeInfo(code, $"Unknown alarm {code}",
            $"The controller reported ALARM:{code}, which is not in the GRBL 1.1 table.",
            "Send $X to unlock and re-home, then check your firmware documentation.");
}

/// <summary>
/// Real-time single bytes. These must be written ahead of anything queued —
/// that is the entire point of them, and a feed hold that waits behind 40 KB of
/// buffered raster is not a feed hold.
/// </summary>
public static class Realtime
{
    public const byte Status = (byte)'?';
    public const byte CycleStart = (byte)'~';
    public const byte FeedHold = (byte)'!';
    public const byte SoftReset = 0x18;
    public const byte SafetyDoor = 0x84;
    public const byte JogCancel = 0x85;

    public const byte FeedOverride100 = 0x90;
    public const byte FeedOverridePlus10 = 0x91;
    public const byte FeedOverrideMinus10 = 0x92;
    public const byte FeedOverridePlus1 = 0x93;
    public const byte FeedOverrideMinus1 = 0x94;

    public const byte RapidOverride100 = 0x95;
    public const byte RapidOverride50 = 0x96;
    public const byte RapidOverride25 = 0x97;

    public const byte SpindleOverride100 = 0x99;
    public const byte SpindleOverridePlus10 = 0x9A;
    public const byte SpindleOverrideMinus10 = 0x9B;
    public const byte SpindleOverridePlus1 = 0x9C;
    public const byte SpindleOverrideMinus1 = 0x9D;
    public const byte SpindleStop = 0x9E;

    public const byte FloodToggle = 0xA0;
    public const byte MistToggle = 0xA1;

    public static bool IsRealtime(byte b) =>
        b is Status or CycleStart or FeedHold or SoftReset || (b >= 0x84 && b <= 0xA1);
}
