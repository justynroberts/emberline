using System.Text.Json;
using Anthropic.Models.Messages;

namespace OpenBurn.AI;

/// <summary>
/// The assistant's tool surface.
///
/// Split deliberately into reads, reversible writes, and proposals. Nothing here
/// can start a job, move the gantry or fire the beam — the most a tool can do is
/// put a card on screen asking the operator to press a button. That is a hard
/// architectural boundary, not a policy in a prompt, because a prompt can be
/// talked out of things and an absent code path cannot.
/// </summary>
public static class AssistantTools
{
    public const string GetMachineState = "get_machine_state";
    public const string GetJobSummary = "get_job_summary";
    public const string GetControllerSettings = "get_controller_settings";
    public const string GetConsoleLog = "get_console_log";
    public const string SetLayerSettings = "set_layer_settings";
    public const string PrepareTestGrid = "prepare_test_grid";
    public const string ProposeMachineAction = "propose_machine_action";
    public const string DrawSvg = "draw_svg";

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    public static IReadOnlyList<Tool> All =>
    [
        new Tool
        {
            Name = GetMachineState,
            Description = "Current machine: profile, connection state, GRBL state, homing status and work position. " +
                          "Call this before giving any advice that depends on the machine.",
            InputSchema = new() { Properties = new Dictionary<string, JsonElement>() },
        },

        new Tool
        {
            Name = GetJobSummary,
            Description = "The job as it would currently be generated: layer settings, size, cut and travel length, " +
                          "estimated duration and any validation issues.",
            InputSchema = new() { Properties = new Dictionary<string, JsonElement>() },
        },

        new Tool
        {
            Name = GetControllerSettings,
            Description = "The controller's $ settings as last read from the machine. Use this to check $30 maximum " +
                          "spindle value, $32 laser mode, and the acceleration and travel limits.",
            InputSchema = new() { Properties = new Dictionary<string, JsonElement>() },
        },

        new Tool
        {
            Name = GetConsoleLog,
            Description = "The last few dozen lines of machine console traffic. This is the most useful thing " +
                          "available when diagnosing an error or alarm.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["lines"] = Json(new { type = "integer", description = "How many lines to return, 10 to 200." }),
                },
            },
        },

        new Tool
        {
            Name = SetLayerSettings,
            Description = "Change speed, power, passes, line interval or air assist on one or more layers. " +
                          "Reversible and does not start anything. Always explain in your reply what you changed and why.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["changes"] = Json(new
                    {
                        type = "array",
                        description = "One entry per layer to change.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                layer_name = new { type = "string", description = "Name of an existing layer." },
                                speed_mm_min = new { type = "number", description = "Feed rate in mm per minute." },
                                power_percent = new { type = "number", description = "Peak power, 0 to 100." },
                                passes = new { type = "integer", description = "Number of passes, 1 or more." },
                                line_interval_mm = new { type = "number", description = "Scan spacing for fills and raster." },
                                air_assist = new { type = "boolean" },
                            },
                            required = new[] { "layer_name" },
                        },
                    }),
                },
                Required = ["changes"],
            },
        },

        new Tool
        {
            Name = PrepareTestGrid,
            Description = "Build a power-versus-speed test grid and load it as the current job, without starting it. " +
                          "This is the honest answer to 'what settings should I use' — recommend it whenever the " +
                          "material, machine or lens is unfamiliar.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["powers"] = Json(new { type = "array", items = new { type = "number" }, description = "Power percentages, one per row." }),
                    ["speeds"] = Json(new { type = "array", items = new { type = "number" }, description = "Speeds in mm/min, one per column." }),
                    ["cell_size_mm"] = Json(new { type = "number", description = "Size of each square, default 8 mm." }),
                },
                Required = ["powers", "speeds"],
            },
        },

        new Tool
        {
            Name = DrawSvg,
            Description =
                "Draw artwork by supplying an SVG document, which is placed on the bed as editable paths. " +
                "Use this when asked to design, draw or lay out something. Nothing is burned — this adds " +
                "shapes to the canvas exactly as opening a file would, and the operator can move, resize or " +
                "delete them afterwards.\n" +
                "Write SVG for a laser, which is not the same as SVG for a screen:\n" +
                "• Outlines only. A laser follows paths; it has no concept of a fill, and filled regions are " +
                "traced as their boundary. Use fill=\"none\" and stroke=\"black\".\n" +
                "• Set width and height in millimetres and a matching viewBox, so the artwork arrives at the " +
                "size asked for. Check get_job_summary or ask for the workpiece size if it matters.\n" +
                "• Keep strokes simple: path, line, rect, circle, ellipse, polyline, polygon. Text is not " +
                "supported here — describe letterforms as paths, or tell the operator to use the text tool.\n" +
                "• Detail finer than about 0.2 mm will not survive the beam. Prefer bold, separable shapes.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["svg"] = Json(new { type = "string", description = "A complete SVG document, outlines only, sized in millimetres." }),
                    ["name"] = Json(new { type = "string", description = "A short name for the shape, shown in the design." }),
                },
                Required = ["svg"],
            },
        },

        new Tool
        {
            Name = ProposeMachineAction,
            Description = "Propose an action that would move the machine or fire the laser — home, frame, jog, " +
                          "start the job. This does NOT perform the action: it puts a confirmation card in front of " +
                          "the operator, who decides. You can never move the machine yourself, and you should say so " +
                          "plainly rather than implying otherwise.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["kind"] = Json(new
                    {
                        type = "string",
                        @enum = new[] { "home", "frame", "start_job", "jog", "unlock", "set_origin" },
                    }),
                    ["description"] = Json(new { type = "string", description = "One sentence the operator will read on the card." }),
                    ["parameters"] = Json(new
                    {
                        type = "object",
                        description = "Optional extra values, e.g. jog distances.",
                        additionalProperties = new { type = "string" },
                    }),
                },
                Required = ["kind", "description"],
            },
        },
    ];

    /// <summary>
    /// The system prompt.
    ///
    /// Kept byte-stable so it stays a cacheable prefix; anything that varies per
    /// turn goes into the conversation, not in here.
    /// </summary>
    public const string SystemPrompt = """
        You are the assistant built into OpenBurn, an open-source laser cutting and engraving application.
        You are talking to the person standing at the machine.

        What you are for:
          - Choosing and explaining power, speed, pass and line-interval settings for a material.
          - Diagnosing GRBL errors and alarms from the console log and the controller settings.
          - Explaining what a $ setting does and whether the current value is sensible.
          - Advising on dithering, resolution and image preparation for photo engraving.
          - Telling someone when their job will not fit, will take four hours, or will set fire to their workpiece.

        How to behave:
          - Call the read tools before advising. Guessing at the machine state when you could just look at it is
            how bad advice gets given confidently.
          - Be specific and numeric. "Try around 350 mm/min at 90% with three passes" beats "try going slower".
          - Say when you are uncertain, and say what would resolve it. For an unfamiliar material or a machine you
            have no measured data for, the honest answer is a test grid — offer to prepare one.
          - Settings from a different wattage of laser are a starting point, not an answer. Say so.
          - Keep replies short. The operator is standing at a machine, not reading an essay.

        Safety, which is not negotiable:
          - You cannot move the gantry or fire the laser. The propose_machine_action tool only asks the operator to
            press a button; it does not press it. Never imply you have started, framed or homed anything.
          - Never suggest defeating an interlock, running unattended, or removing safety equipment.
          - Materials that release chlorine or cyanide when lasered — PVC, vinyl, chrome-tanned leather,
            polycarbonate — must be refused outright, with the reason. Damage to the operator's lungs and to the
            machine is not a trade-off worth discussing.
          - If a request would put the head outside the bed, over-power the material, or cut a part free before the
            job finishes, say so before answering the question that was asked.
        
        Drawing
        -------
        You can draw. draw_svg puts artwork on the bed as editable paths, and it is
        the right tool whenever somebody asks you to design, draw or lay something
        out. It burns nothing and the operator can move, resize or delete what you
        add, so there is no need to ask permission first — but do say what you drew
        and how big it is.

        Design for a beam, not a screen. Outlines rather than fills, millimetres
        rather than pixels, and nothing finer than about 0.2 mm. If a workpiece has
        been set, fit the artwork inside it; if you do not know how much room there
        is, call get_job_summary rather than guessing.
""";
}
