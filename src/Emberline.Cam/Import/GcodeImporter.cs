using Emberline.Core.Documents;
using Emberline.Core.Geometry;
using Emberline.GCode;

namespace Emberline.Cam.Import;

public sealed record GcodeImportResult(
    IReadOnlyList<string> Lines,
    Toolpath Toolpath,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Loading an existing G-code file.
///
/// Two distinct jobs: keep the original lines byte-for-byte so the machine gets
/// exactly what the file said, and separately interpret them so the canvas can
/// draw a preview. Regenerating G-code from the preview would quietly rewrite
/// somebody's carefully tuned output, so Emberline never does that.
/// </summary>
public static class GcodeImporter
{
    public static readonly string[] SupportedExtensions = [".nc", ".gcode", ".gc", ".tap", ".ngc", ".cnc", ".txt"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static GcodeImportResult Load(string path, double maxSpindle = 1000)
    {
        var text = File.ReadAllText(path);
        return Parse(text, maxSpindle);
    }

    public static GcodeImportResult Parse(string text, double maxSpindle = 1000)
    {
        var rawLines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var toolpath = GcodeInterpreter.Interpret(rawLines, new InterpreterOptions { MaxSpindle = maxSpindle });

        var warnings = new List<string>();
        if (toolpath.IsInches)
        {
            warnings.Add("This file is in inch mode (G20). Emberline converted it for the preview; the controller will need to be in G20 as well.");
        }
        if (!toolpath.UsesLaser)
        {
            warnings.Add("This file never switches the laser on with M3 or M4, so it will trace the path without burning.");
        }
        if (toolpath.MaxSpindleSeen > maxSpindle)
        {
            warnings.Add($"This file uses S values up to {toolpath.MaxSpindleSeen:0}, which is above the machine's maximum of S{maxSpindle:0}.");
        }

        warnings.AddRange(toolpath.Warnings.Take(5).Select(w => $"Line {w.LineIndex + 1}: {w.Text}"));

        return new GcodeImportResult(rawLines, toolpath, warnings);
    }

    /// <summary>
    /// Build a preview shape from an interpreted toolpath, so imported G-code can be
    /// seen and positioned on the canvas alongside real artwork.
    /// </summary>
    public static PathShape ToPreviewShape(Toolpath toolpath, string name = "G-code")
    {
        var paths = new List<Polyline>();
        Polyline? current = null;
        var lastX = double.NaN;
        var lastY = double.NaN;

        for (var i = 0; i < toolpath.Count; i++)
        {
            if (toolpath.Rapid[i])
            {
                // A rapid breaks the burning path; the preview should not draw travel.
                if (current is { Count: > 1 }) paths.Add(current);
                current = null;
                lastX = toolpath.X1[i];
                lastY = toolpath.Y1[i];
                continue;
            }

            var startsNewPath = current is null ||
                                Math.Abs(toolpath.X0[i] - lastX) > 1e-6 ||
                                Math.Abs(toolpath.Y0[i] - lastY) > 1e-6;

            if (startsNewPath)
            {
                if (current is { Count: > 1 }) paths.Add(current);
                current = new Polyline();
                current.Add(toolpath.X0[i], toolpath.Y0[i]);
            }

            current!.Add(toolpath.X1[i], toolpath.Y1[i]);
            lastX = toolpath.X1[i];
            lastY = toolpath.Y1[i];
        }

        if (current is { Count: > 1 }) paths.Add(current);

        return new PathShape(paths) { Name = name };
    }
}
