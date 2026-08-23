using System.Globalization;
using OpenBurn.Core.Geometry;

namespace OpenBurn.GCode;

public sealed record InterpreterOptions
{
    /// <summary>Arc chord tolerance in mm. Mirrors GRBL's $12.</summary>
    public double ArcTolerance { get; init; } = 0.002;

    /// <summary>The S value that means full power. Mirrors GRBL's $30.</summary>
    public double MaxSpindle { get; init; } = 1000;

    public static readonly InterpreterOptions Default = new();
}

/// <summary>
/// Interprets a G-code program into a <see cref="Toolpath"/>.
///
/// Supports the subset GRBL actually implements — G0/G1/G2/G3, G17/18/19,
/// G20/G21, G90/G91, G90.1/G91.1, G92, M3/M4/M5, and the F/S words. Anything
/// else is recorded as a warning rather than silently ignored, because a preview
/// that quietly drops a command is worse than one that says it could not read it.
/// </summary>
public static class GcodeInterpreter
{
    public static Toolpath Interpret(string source, InterpreterOptions? options = null) =>
        Interpret(source.Split('\n'), options);

    public static Toolpath Interpret(IReadOnlyList<string> lines, InterpreterOptions? options = null)
    {
        var o = options ?? InterpreterOptions.Default;
        var tp = new Toolpath();

        var motion = 0;              // 0=G0 1=G1 2=G2 3=G3
        var absolute = true;
        var arcAbsolute = false;     // G90.1 makes I/J absolute; incremental is the default
        var unitScale = 1.0;
        var x = 0.0;
        var y = 0.0;
        var z = 0.0;
        var feed = 0.0;
        var spindle = 0.0;
        var laserOn = false;
        var offsetX = 0.0;
        var offsetY = 0.0;

        tp.SeedBounds(0, 0);

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.Length == 0) continue;

            var hasAxis = false;
            var targetX = x;
            var targetY = y;
            var targetZ = z;
            var i = 0.0;
            var j = 0.0;
            var radius = double.NaN;
            var hasIJ = false;
            var hasRadius = false;
            int? motionThisLine = null;
            var isG92 = false;
            var sawUnsupported = (string?)null;

            foreach (var (letter, value) in Words(line))
            {
                switch (letter)
                {
                    case 'G':
                    {
                        var g = Math.Round(value, 1);
                        if (g is 0 or 1 or 2 or 3) motionThisLine = (int)g;
                        else if (g == 20) { unitScale = 25.4; tp.IsInches = true; }
                        else if (g == 21) { unitScale = 1; tp.IsInches = false; }
                        else if (g == 90) absolute = true;
                        else if (g == 91) absolute = false;
                        else if (Math.Abs(g - 90.1) < 0.001) arcAbsolute = true;
                        else if (Math.Abs(g - 91.1) < 0.001) arcAbsolute = false;
                        else if (g == 92) isG92 = true;
                        else if (g is 4 or 17 or 18 or 19 or 28 or 30 or 53 or 54 or 55 or 56 or 57 or 58 or 59 or 94 or 93) { /* no path contribution */ }
                        else sawUnsupported = $"G{value.ToString("0.###", CultureInfo.InvariantCulture)}";
                        break;
                    }
                    case 'M':
                    {
                        var m = (int)Math.Round(value);
                        if (m is 3 or 4) { laserOn = true; tp.UsesLaser = true; }
                        else if (m == 5) laserOn = false;
                        break;
                    }
                    case 'X': targetX = absolute ? value * unitScale + offsetX : x + value * unitScale; hasAxis = true; break;
                    case 'Y': targetY = absolute ? value * unitScale + offsetY : y + value * unitScale; hasAxis = true; break;
                    case 'Z': targetZ = absolute ? value * unitScale : z + value * unitScale; hasAxis = true; break;
                    case 'I': i = value * unitScale; hasIJ = true; break;
                    case 'J': j = value * unitScale; hasIJ = true; break;
                    case 'R': radius = value * unitScale; hasRadius = true; break;
                    case 'F': feed = value * unitScale; break;
                    case 'S':
                        spindle = value;
                        if (value > tp.MaxSpindleSeen) tp.MaxSpindleSeen = value;
                        break;
                }
            }

            if (sawUnsupported is not null)
            {
                tp.Warnings.Add(new ToolpathWarning(lineIndex, $"Unsupported command {sawUnsupported} — ignored in preview."));
            }

            if (isG92)
            {
                // G92 redefines the current position, which shifts the work offset.
                offsetX += x - targetX;
                offsetY += y - targetY;
                z = targetZ;
                continue;
            }

            if (motionThisLine is { } m2) motion = m2;
            if (!hasAxis) { z = targetZ; continue; }

            var power = laserOn && motion != 0 ? (float)Math.Clamp(spindle / Math.Max(o.MaxSpindle, 1), 0, 1) : 0f;
            var rapid = motion == 0 || power <= 0;

            switch (motion)
            {
                case 0:
                case 1:
                    tp.Add(x, y, targetX, targetY, power, (float)feed, rapid, lineIndex);
                    x = targetX;
                    y = targetY;
                    break;

                case 2:
                case 3:
                {
                    var clockwise = motion == 2;
                    double cx, cy;

                    if (hasIJ)
                    {
                        cx = arcAbsolute ? i : x + i;
                        cy = arcAbsolute ? j : y + j;
                    }
                    else if (hasRadius && !double.IsNaN(radius))
                    {
                        if (!TryCentreFromRadius(x, y, targetX, targetY, radius, clockwise, out cx, out cy))
                        {
                            tp.Warnings.Add(new ToolpathWarning(lineIndex, "Arc radius is too small for its endpoints; drawn as a straight line."));
                            tp.Add(x, y, targetX, targetY, power, (float)feed, rapid, lineIndex);
                            x = targetX; y = targetY; z = targetZ;
                            continue;
                        }
                    }
                    else
                    {
                        tp.Warnings.Add(new ToolpathWarning(lineIndex, "Arc with no I/J offsets and no R radius; drawn as a straight line."));
                        tp.Add(x, y, targetX, targetY, power, (float)feed, rapid, lineIndex);
                        x = targetX; y = targetY; z = targetZ;
                        continue;
                    }

                    EmitArc(tp, x, y, targetX, targetY, cx, cy, clockwise, o.ArcTolerance, power, (float)feed, rapid, lineIndex,
                            ref x, ref y);
                    break;
                }
            }

            z = targetZ;
        }

        return tp;
    }

    /// <summary>Yield the letter/number word pairs on a line, skipping comments.</summary>
    public static IEnumerable<(char Letter, double Value)> Words(string line)
    {
        var inParens = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '(') { inParens = true; continue; }
            if (ch == ')') { inParens = false; continue; }
            if (inParens) continue;
            if (ch == ';') yield break;
            if (!char.IsLetter(ch)) continue;

            var letter = char.ToUpperInvariant(ch);
            var start = i + 1;
            while (start < line.Length && char.IsWhiteSpace(line[start])) start++;

            var end = start;
            if (end < line.Length && (line[end] == '-' || line[end] == '+')) end++;
            var sawDigit = false;
            while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '.'))
            {
                if (char.IsDigit(line[end])) sawDigit = true;
                end++;
            }

            if (!sawDigit) continue;
            if (double.TryParse(line.AsSpan(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                yield return (letter, value);
            }
            i = end - 1;
        }
    }

    /// <summary>
    /// Solve the arc centre for R-format arcs. A negative R selects the major arc.
    /// Returns false when the radius is too small to span the endpoints at all.
    /// </summary>
    public static bool TryCentreFromRadius(double x0, double y0, double x1, double y1, double r, bool clockwise,
                                           out double cx, out double cy)
    {
        cx = cy = 0;
        var dx = x1 - x0;
        var dy = y1 - y0;
        var d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-12) return false;

        var hSquared = r * r - d * d / 4;
        if (hSquared < -1e-9) return false;

        var h = Math.Sqrt(Math.Max(0, hSquared));
        var mx = (x0 + x1) / 2;
        var my = (y0 + y1) / 2;
        var sign = (clockwise ? -1 : 1) * (r < 0 ? -1 : 1);

        cx = mx + sign * h * -dy / d;
        cy = my + sign * h * dx / d;
        return true;
    }

    private static void EmitArc(Toolpath tp, double x0, double y0, double x1, double y1, double cx, double cy,
                                bool clockwise, double tolerance, float power, float feed, bool rapid, int lineIndex,
                                ref double curX, ref double curY)
    {
        var r = Math.Sqrt((x0 - cx) * (x0 - cx) + (y0 - cy) * (y0 - cy));
        if (r < 1e-9)
        {
            tp.Add(x0, y0, x1, y1, power, feed, rapid, lineIndex);
            curX = x1;
            curY = y1;
            return;
        }

        var a0 = Math.Atan2(y0 - cy, x0 - cx);
        var a1 = Math.Atan2(y1 - cy, x1 - cx);
        var sweep = a1 - a0;

        if (clockwise)
        {
            while (sweep >= 0) sweep -= Math.PI * 2;
            if (sweep <= -Math.PI * 2) sweep += Math.PI * 2;
        }
        else
        {
            while (sweep <= 0) sweep += Math.PI * 2;
            if (sweep >= Math.PI * 2) sweep -= Math.PI * 2;
        }

        // Coincident endpoints mean a full circle, which the wrap logic above collapses to zero.
        if (Math.Abs(x1 - x0) < 1e-9 && Math.Abs(y1 - y0) < 1e-9) sweep = clockwise ? -Math.PI * 2 : Math.PI * 2;

        var maxStep = 2 * Math.Acos(Math.Clamp(1 - tolerance / r, -1, 1));
        if (double.IsNaN(maxStep) || maxStep <= 1e-6) maxStep = 0.1;
        var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / maxStep));

        var px = x0;
        var py = y0;
        for (var s = 1; s <= steps; s++)
        {
            var a = a0 + sweep * s / steps;
            var (sin, cos) = Math.SinCos(a);
            var nx = cx + r * cos;
            var ny = cy + r * sin;
            tp.Add(px, py, nx, ny, power, feed, rapid, lineIndex);
            px = nx;
            py = ny;
        }

        // Land exactly on the commanded endpoint so error cannot accumulate over many arcs.
        if (Math.Abs(px - x1) > 1e-9 || Math.Abs(py - y1) > 1e-9)
        {
            tp.Add(px, py, x1, y1, power, feed, rapid, lineIndex);
        }

        curX = x1;
        curY = y1;
    }
}
