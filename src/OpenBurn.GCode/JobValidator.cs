using OpenBurn.Core.Geometry;
using OpenBurn.Core.Machines;

namespace OpenBurn.GCode;

public enum ValidationSeverity { Info, Warning, Error }

public sealed record ValidationIssue(ValidationSeverity Severity, string Title, string Detail, int? LineIndex = null)
{
    public override string ToString() => LineIndex is { } l ? $"[{Severity}] line {l + 1}: {Title} — {Detail}" : $"[{Severity}] {Title} — {Detail}";
}

/// <summary>
/// Pre-flight checks, per the simulation requirements in the PRD.
///
/// Everything here is something that has actually cost somebody a workpiece: a job
/// that runs off the bed, a feed rate the machine cannot reach, arcs the firmware
/// will reject, or a cut ordered so the part falls out before the job finishes.
/// </summary>
public static class JobValidator
{
    public static List<ValidationIssue> Validate(
        Toolpath toolpath,
        MachineProfile machine,
        bool isHomed = true,
        IReadOnlyDictionary<int, double>? machineSettings = null)
    {
        var issues = new List<ValidationIssue>();
        var bed = new Rect2(0, 0, machine.BedWidthMm, machine.BedHeightMm);

        if (toolpath.Count == 0)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Nothing to run",
                "This job contains no motion. Check that at least one layer is enabled and has geometry on it."));
            return issues;
        }

        // --- Bounds -------------------------------------------------------
        var bounds = toolpath.Bounds;
        if (!bed.Contains(bounds))
        {
            var overX = Math.Max(0, Math.Max(-bounds.MinX, bounds.MaxX - bed.MaxX));
            var overY = Math.Max(0, Math.Max(-bounds.MinY, bounds.MaxY - bed.MaxY));
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Job is outside the machine bed",
                $"The toolpath spans {bounds} but the bed is {machine.BedWidthMm:0}×{machine.BedHeightMm:0} mm. " +
                $"It overruns by {overX:0.#} mm in X and {overY:0.#} mm in Y. Move or resize the artwork."));
        }
        else if (bounds.MinX < 1 || bounds.MinY < 1 ||
                 bounds.MaxX > machine.BedWidthMm - 1 || bounds.MaxY > machine.BedHeightMm - 1)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Job is very close to the bed edge",
                "There is less than 1 mm of margin. If the work origin is even slightly off, the job will hit a limit."));
        }

        // --- Coordinates --------------------------------------------------
        var x1 = toolpath.X1;
        var y1 = toolpath.Y1;
        for (var i = 0; i < toolpath.Count; i++)
        {
            if (double.IsFinite(x1[i]) && double.IsFinite(y1[i])) continue;
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "Invalid coordinate",
                "A move resolved to a non-finite coordinate, which usually means a malformed arc or a divide by zero in the source file.",
                toolpath.SourceLine[i]));
            break;
        }

        // --- Speed --------------------------------------------------------
        var feeds = toolpath.Feed;
        var maxFeed = 0f;
        for (var i = 0; i < toolpath.Count; i++)
        {
            if (!toolpath.Rapid[i] && feeds[i] > maxFeed) maxFeed = feeds[i];
        }

        if (maxFeed > machine.MaxSpeedMmMin)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Requested speed exceeds the machine maximum",
                $"The job asks for {maxFeed:0} mm/min but {machine.DisplayName} tops out at {machine.MaxSpeedMmMin:0} mm/min. " +
                "The controller will clamp it, so the real burn will be darker than the preview suggests."));
        }

        if (maxFeed == 0 && toolpath.UsesLaser)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "No feed rate set",
                "The job fires the laser but never sets an F word. GRBL will answer error:22 on the first cut move."));
        }

        // --- Power --------------------------------------------------------
        if (toolpath.MaxSpindleSeen > machine.MaxSpindleValue)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Power value above the machine maximum",
                $"The job uses S{toolpath.MaxSpindleSeen:0} but the profile says maximum is S{machine.MaxSpindleValue}. " +
                "Check that the profile matches the controller's $30 setting."));
        }

        if (!toolpath.UsesLaser)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Laser is never switched on",
                "No M3 or M4 appears in this job, so the head will trace the path without burning anything."));
        }

        // --- Units --------------------------------------------------------
        if (toolpath.IsInches)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Info, "Job is in inches",
                "The file ends in G20 (inch) mode. OpenBurn has converted for the preview, but the controller will also need to be in G20."));
        }

        // --- Homing -------------------------------------------------------
        if (!isHomed && machine.Capabilities.HasFlag(MachineCapabilities.Homing))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Machine has not been homed",
                "Without homing, the machine has no absolute reference and soft limits cannot protect the job. Home first, or set the work origin by hand and accept the risk."));
        }

        // --- Interpreter warnings ------------------------------------------
        foreach (var w in toolpath.Warnings.Take(10))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "Unrecognised G-code", w.Text, w.LineIndex));
        }

        if (toolpath.Warnings.Count > 10)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Info, "More warnings suppressed",
                $"{toolpath.Warnings.Count - 10} further interpreter warnings were not listed."));
        }

        // --- Controller settings -------------------------------------------
        if (machineSettings is not null)
        {
            foreach (var w in Grbl.GrblSettings.Audit(machineSettings))
            {
                issues.Add(new ValidationIssue(
                    w.IsError ? ValidationSeverity.Error : ValidationSeverity.Warning,
                    $"Controller setting ${w.Key}", w.Text));
            }
        }

        return issues;
    }

    public static bool HasBlockingIssue(IEnumerable<ValidationIssue> issues) =>
        issues.Any(i => i.Severity == ValidationSeverity.Error);
}
