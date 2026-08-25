using System.Globalization;
using Emberline.Core.Documents;
using Emberline.Core.Geometry;

namespace Emberline.Cam.Import;

public sealed record DxfImportResult(
    IReadOnlyList<Polyline> Paths,
    double WidthMm,
    double HeightMm,
    IReadOnlyList<string> Warnings,
    string Units);

/// <summary>
/// DXF import.
///
/// ASCII DXF only, which is what CAD packages and every online parts source
/// actually emit; binary DXF is rare enough that supporting it badly would be
/// worse than not supporting it.
///
/// The two things that decide whether an import is usable are the same as for SVG:
/// real-world size and orientation. DXF is already Y-up and already in real units,
/// which makes it the easier of the two — the work is in the entity coverage,
/// because a drawing that silently drops its arcs looks fine until it is cut.
/// </summary>
public static class DxfImporter
{
    public static readonly string[] SupportedExtensions = [".dxf"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static DxfImportResult Load(string path, double tolerance = Curves.DefaultTolerance) =>
        Parse(File.ReadAllText(path), tolerance);

    public static DxfImportResult Parse(string text, double tolerance = Curves.DefaultTolerance)
    {
        var pairs = ReadPairs(text);
        var warnings = new List<string>();

        var (scale, unitName) = ReadUnits(pairs, warnings);
        var blocks = ReadBlocks(pairs, tolerance, warnings);
        var paths = ReadEntities(pairs, blocks, tolerance, warnings);

        // Scale from drawing units to millimetres.
        if (Math.Abs(scale - 1) > 1e-9)
        {
            var matrix = Matrix2D.Scale(scale);
            paths = [.. paths.Select(p => p.Transformed(matrix))];
        }

        if (paths.Count == 0) warnings.Add("No drawable entities were found in this DXF.");

        var bounds = Rect2.FromPoints(paths.SelectMany(p => p.Points));

        return new DxfImportResult(
            paths,
            bounds.IsEmpty ? 0 : bounds.Width,
            bounds.IsEmpty ? 0 : bounds.Height,
            warnings,
            unitName);
    }

    /// <summary>
    /// DXF is a flat list of (group code, value) pairs. Everything else is
    /// interpretation of that list.
    /// </summary>
    private static List<(int Code, string Value)> ReadPairs(string text)
    {
        var pairs = new List<(int, string)>();
        var lines = text.Split('\n');

        for (var i = 0; i + 1 < lines.Length; i += 2)
        {
            var codeText = lines[i].Trim();
            if (codeText.Length == 0)
            {
                // A stray blank line would desynchronise every pair after it, so
                // resynchronise rather than reading the file shifted by one.
                i--;
                continue;
            }

            if (!int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)) continue;
            pairs.Add((code, lines[i + 1].TrimEnd('\r', '\n')));
        }

        return pairs;
    }

    /// <summary>Drawing units to millimetres, from $INSUNITS in the header.</summary>
    private static (double Scale, string Name) ReadUnits(List<(int Code, string Value)> pairs, List<string> warnings)
    {
        for (var i = 0; i < pairs.Count - 2; i++)
        {
            if (pairs[i].Code != 9 || pairs[i].Value.Trim() != "$INSUNITS") continue;

            for (var j = i + 1; j < Math.Min(i + 4, pairs.Count); j++)
            {
                if (pairs[j].Code != 70) continue;
                if (!int.TryParse(pairs[j].Value.Trim(), out var code)) break;

                return code switch
                {
                    1 => (25.4, "inches"),
                    2 => (304.8, "feet"),
                    4 => (1.0, "millimetres"),
                    5 => (10.0, "centimetres"),
                    6 => (1000.0, "metres"),
                    0 => Unitless(warnings),
                    _ => (1.0, "millimetres"),
                };
            }
            break;
        }

        return Unitless(warnings);

        static (double, string) Unitless(List<string> w)
        {
            w.Add("This DXF does not declare its units, so the drawing was read as millimetres. " +
                  "Check the imported size before burning.");
            return (1.0, "assumed millimetres");
        }
    }

    private sealed record Entity(string Type, Dictionary<int, List<string>> Values)
    {
        public double Number(int code, int index = 0, double fallback = 0)
        {
            if (!Values.TryGetValue(code, out var list) || index >= list.Count) return fallback;
            return double.TryParse(list[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        public int Integer(int code, int fallback = 0) => (int)Number(code, 0, fallback);

        public string Text(int code, string fallback = "") =>
            Values.TryGetValue(code, out var list) && list.Count > 0 ? list[0] : fallback;

        public IReadOnlyList<string> All(int code) =>
            Values.TryGetValue(code, out var list) ? list : [];

        public int CountOf(int code) => Values.TryGetValue(code, out var list) ? list.Count : 0;
    }

    /// <summary>Split a section into entities, each starting at a group-0 marker.</summary>
    private static List<Entity> ReadSection(List<(int Code, string Value)> pairs, string sectionName)
    {
        var entities = new List<Entity>();
        var inside = false;
        Entity? current = null;

        for (var i = 0; i < pairs.Count; i++)
        {
            var (code, value) = pairs[i];
            var trimmed = value.Trim();

            if (code == 0 && trimmed == "SECTION")
            {
                // The next 2-code names the section.
                var name = i + 1 < pairs.Count && pairs[i + 1].Code == 2 ? pairs[i + 1].Value.Trim() : string.Empty;
                inside = string.Equals(name, sectionName, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (code == 0 && trimmed == "ENDSEC")
            {
                if (current is not null) entities.Add(current);
                current = null;
                inside = false;
                continue;
            }

            if (!inside) continue;

            if (code == 0)
            {
                if (current is not null) entities.Add(current);
                current = new Entity(trimmed, []);
                continue;
            }

            if (current is null) continue;

            if (!current.Values.TryGetValue(code, out var list))
            {
                list = [];
                current.Values[code] = list;
            }
            list.Add(value.Trim());
        }

        if (current is not null) entities.Add(current);
        return entities;
    }

    /// <summary>Named blocks, so INSERT references can be expanded.</summary>
    private static Dictionary<string, List<Polyline>> ReadBlocks(
        List<(int Code, string Value)> pairs, double tolerance, List<string> warnings)
    {
        var blocks = new Dictionary<string, List<Polyline>>(StringComparer.OrdinalIgnoreCase);
        var entities = ReadSection(pairs, "BLOCKS");

        string? currentName = null;
        var currentPaths = new List<Polyline>();

        foreach (var entity in entities)
        {
            switch (entity.Type)
            {
                case "BLOCK":
                    if (currentName is not null) blocks[currentName] = currentPaths;
                    currentName = entity.Text(2);
                    currentPaths = [];
                    break;

                case "ENDBLK":
                    if (currentName is not null) blocks[currentName] = currentPaths;
                    currentName = null;
                    currentPaths = [];
                    break;

                default:
                    if (currentName is null) break;
                    currentPaths.AddRange(Convert(entity, tolerance, warnings, blocks, depth: 0));
                    break;
            }
        }

        if (currentName is not null) blocks[currentName] = currentPaths;
        return blocks;
    }

    private static List<Polyline> ReadEntities(
        List<(int Code, string Value)> pairs,
        Dictionary<string, List<Polyline>> blocks,
        double tolerance,
        List<string> warnings)
    {
        var result = new List<Polyline>();
        var unsupported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var entities = ReadSection(pairs, "ENTITIES");

        for (var i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];

            // Old-style POLYLINE stores its points as separate VERTEX entities that
            // follow it until SEQEND. Anything that reads entities independently
            // silently drops every such polyline, and plenty of CAD output uses them.
            if (entity.Type == "POLYLINE")
            {
                var closed = (entity.Integer(70) & 1) == 1;
                var poly = new Polyline { IsClosed = closed };
                var previousBulge = 0.0;
                var havePrevious = false;
                var previousPoint = Vec2.Zero;

                var j = i + 1;
                for (; j < entities.Count && entities[j].Type == "VERTEX"; j++)
                {
                    var vertex = entities[j];
                    var point = new Vec2(vertex.Number(10), vertex.Number(20));

                    if (havePrevious && Math.Abs(previousBulge) > 1e-9)
                    {
                        AppendBulge(poly, previousPoint, point, previousBulge, tolerance);
                    }

                    poly.Add(point);
                    previousPoint = point;
                    previousBulge = vertex.Number(42);
                    havePrevious = true;
                }

                if (closed && havePrevious && Math.Abs(previousBulge) > 1e-9 && poly.Count > 1)
                {
                    AppendBulge(poly, previousPoint, poly.First, previousBulge, tolerance);
                }

                // Skip the vertices and the SEQEND that closes them.
                i = j < entities.Count && entities[j].Type == "SEQEND" ? j : j - 1;

                if (poly.Count > 1) result.Add(poly);
                continue;
            }

            var converted = Convert(entity, tolerance, warnings, blocks, depth: 0);
            if (converted.Count > 0) result.AddRange(converted);
            else if (entity.Type is not ("SEQEND" or "VERTEX" or "ENDBLK" or "BLOCK" or "POINT")) unsupported.Add(entity.Type);
        }

        if (unsupported.Count > 0)
        {
            warnings.Add($"These entity types were skipped: {string.Join(", ", unsupported.Order())}. " +
                         "Explode them to lines, arcs and polylines in your CAD package before exporting.");
        }

        return result;
    }

    private static List<Polyline> Convert(
        Entity entity,
        double tolerance,
        List<string> warnings,
        Dictionary<string, List<Polyline>> blocks,
        int depth)
    {
        var result = new List<Polyline>();

        switch (entity.Type)
        {
            case "LINE":
            {
                var line = new Polyline();
                line.Add(entity.Number(10), entity.Number(20));
                line.Add(entity.Number(11), entity.Number(21));
                if (line.Count > 1) result.Add(line);
                break;
            }

            case "POINT":
                // A point has no length, so there is nothing for a laser to follow.
                break;

            case "CIRCLE":
            {
                var radius = entity.Number(40);
                if (radius <= 0) break;
                var circle = new Polyline { IsClosed = true };
                Curves.FlattenArc(circle, new Vec2(entity.Number(10), entity.Number(20)), radius, 0, Math.PI * 2, tolerance);
                result.Add(circle);
                break;
            }

            case "ARC":
            {
                var radius = entity.Number(40);
                if (radius <= 0) break;

                var start = entity.Number(50) * Math.PI / 180;
                var end = entity.Number(51) * Math.PI / 180;

                // DXF arcs always run counter-clockwise from start to end.
                var sweep = end - start;
                while (sweep <= 0) sweep += Math.PI * 2;

                var arc = new Polyline();
                Curves.FlattenArc(arc, new Vec2(entity.Number(10), entity.Number(20)), radius, start, sweep, tolerance);
                if (arc.Count > 1) result.Add(arc);
                break;
            }

            case "ELLIPSE":
            {
                var centre = new Vec2(entity.Number(10), entity.Number(20));
                // Group 11/21 is the major axis endpoint, relative to the centre.
                var major = new Vec2(entity.Number(11), entity.Number(21));
                var ratio = entity.Number(40, 0, 1);
                var startParam = entity.Number(41);
                var endParam = entity.Number(42, 0, Math.PI * 2);

                var majorLength = major.Length;
                if (majorLength < 1e-9) break;

                var minorLength = majorLength * ratio;
                var rotation = Math.Atan2(major.Y, major.X);

                var sweep = endParam - startParam;
                while (sweep <= 0) sweep += Math.PI * 2;

                var steps = Math.Max(16, (int)(sweep / (2 * Math.Acos(Math.Clamp(1 - tolerance / majorLength, -1, 1)))));
                var ellipse = new Polyline(steps + 1)
                {
                    IsClosed = Math.Abs(sweep - Math.PI * 2) < 1e-6,
                };

                for (var i = 0; i <= steps; i++)
                {
                    var t = startParam + sweep * i / steps;
                    var (sin, cos) = Math.SinCos(t);
                    var local = new Vec2(majorLength * cos, minorLength * sin).Rotated(rotation);
                    ellipse.Add(centre + local);
                }

                result.Add(ellipse);
                break;
            }

            case "LWPOLYLINE":
            {
                var xs = entity.All(10);
                var ys = entity.All(20);
                var bulges = entity.All(42);
                var count = Math.Min(xs.Count, ys.Count);
                if (count < 2) break;

                var closed = (entity.Integer(70) & 1) == 1;
                var poly = new Polyline { IsClosed = closed };

                for (var i = 0; i < count; i++)
                {
                    var from = new Vec2(ParseDouble(xs[i]), ParseDouble(ys[i]));
                    poly.Add(from);

                    var next = i + 1 < count ? i + 1 : (closed ? 0 : -1);
                    if (next < 0) continue;

                    // A bulge is the tangent of a quarter of the included angle —
                    // this is how DXF stores arc segments inside a polyline, and
                    // ignoring it turns every rounded corner into a chord.
                    var bulge = i < bulges.Count ? ParseDouble(bulges[i]) : 0;
                    if (Math.Abs(bulge) < 1e-9) continue;

                    var to = new Vec2(ParseDouble(xs[next]), ParseDouble(ys[next]));
                    AppendBulge(poly, from, to, bulge, tolerance);
                }

                if (poly.Count > 1) result.Add(poly);
                break;
            }

            case "SPLINE":
            {
                // Fit points give a usable curve; control points alone would need a
                // full NURBS evaluation for a result no laser could tell apart.
                var fitX = entity.All(11);
                var fitY = entity.All(21);

                if (fitX.Count >= 2 && fitY.Count >= 2)
                {
                    var spline = new Polyline((entity.Integer(70) & 1) == 1 ? fitX.Count + 1 : fitX.Count);
                    for (var i = 0; i < Math.Min(fitX.Count, fitY.Count); i++)
                    {
                        spline.Add(ParseDouble(fitX[i]), ParseDouble(fitY[i]));
                    }
                    spline.IsClosed = (entity.Integer(70) & 1) == 1;
                    if (spline.Count > 1) result.Add(PathOps.Smooth(spline, 2));
                    break;
                }

                var controlX = entity.All(10);
                var controlY = entity.All(20);
                if (controlX.Count >= 2 && controlY.Count >= 2)
                {
                    var approximate = new Polyline();
                    for (var i = 0; i < Math.Min(controlX.Count, controlY.Count); i++)
                    {
                        approximate.Add(ParseDouble(controlX[i]), ParseDouble(controlY[i]));
                    }
                    approximate.IsClosed = (entity.Integer(70) & 1) == 1;

                    // Chaikin over the control polygon lands close to the true curve
                    // and is honest about being an approximation.
                    if (approximate.Count > 1) result.Add(PathOps.Smooth(approximate, 3));
                    warnings.Add("A spline was approximated from its control points. " +
                                 "Export with fit points, or convert splines to polylines, for an exact result.");
                }
                break;
            }

            case "INSERT":
            {
                if (depth > 8)
                {
                    warnings.Add("A block reference nests more than eight deep and was skipped.");
                    break;
                }

                var name = entity.Text(2);
                if (!blocks.TryGetValue(name, out var contents)) break;

                var transform =
                    Matrix2D.Translate(entity.Number(10), entity.Number(20)) *
                    Matrix2D.Rotate(entity.Number(50)) *
                    Matrix2D.Scale(entity.Number(41, 0, 1), entity.Number(42, 0, 1));

                foreach (var path in contents) result.Add(path.Transformed(transform));
                break;
            }
        }

        return result;
    }

    /// <summary>Expand a polyline bulge into an arc between two vertices.</summary>
    private static void AppendBulge(Polyline into, Vec2 from, Vec2 to, double bulge, double tolerance)
    {
        var chord = to - from;
        var chordLength = chord.Length;
        if (chordLength < 1e-9) return;

        var includedAngle = 4 * Math.Atan(bulge);
        var radius = chordLength / (2 * Math.Sin(Math.Abs(includedAngle) / 2));
        if (!double.IsFinite(radius) || radius < 1e-9) return;

        // Centre sits perpendicular to the chord, offset by the sagitta complement.
        var midpoint = from + chord * 0.5;
        var height = radius * Math.Cos(includedAngle / 2);
        var perpendicular = new Vec2(-chord.Y, chord.X).Normalized;
        var centre = midpoint + perpendicular * (bulge > 0 ? height : -height);

        var startAngle = Math.Atan2(from.Y - centre.Y, from.X - centre.X);

        var arc = new Polyline();
        Curves.FlattenArc(arc, centre, radius, startAngle, includedAngle, tolerance);

        // The first point duplicates `from`, which Polyline.Add drops anyway.
        for (var i = 1; i < arc.Count - 1; i++) into.Add(arc[i]);
    }

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static PathShape ImportAsShape(string path, double tolerance = Curves.DefaultTolerance)
    {
        var result = Load(path, tolerance);
        return new PathShape(result.Paths) { Name = Path.GetFileNameWithoutExtension(path) };
    }
}
