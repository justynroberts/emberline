using Emberline.Core.Geometry;

namespace Emberline.Cam.Vector;

public sealed record HatchOptions
{
    /// <summary>Distance between hatch lines, millimetres.</summary>
    public double SpacingMm { get; init; } = 0.15;

    /// <summary>Hatch direction in degrees. Zero is horizontal.</summary>
    public double AngleDegrees { get; init; }

    /// <summary>Add a second set of lines at 90° for a denser fill.</summary>
    public bool CrossHatch { get; init; }

    /// <summary>Alternate direction line to line, which halves travel.</summary>
    public bool Bidirectional { get; init; } = true;

    /// <summary>
    /// Inset the fill from the outline by this much, so a separate outline pass does
    /// not double-burn the edge. Roughly half the beam kerf.
    /// </summary>
    public double InsetMm { get; init; }

    public static readonly HatchOptions Default = new();
}

/// <summary>
/// Scan-line fill of closed contours.
///
/// The whole job is: rotate the geometry so the hatch direction is horizontal,
/// intersect a ladder of horizontal lines with every edge, sort the crossings and
/// pair them up, then rotate the resulting spans back. Doing it in rotated space
/// rather than intersecting at an arbitrary angle keeps the maths to one case, and
/// one case is one thing to get right.
/// </summary>
public static class HatchFill
{
    public static List<Polyline> Generate(IReadOnlyList<Polyline> contours, HatchOptions? options = null)
    {
        var o = options ?? HatchOptions.Default;
        var result = GenerateAtAngle(contours, o.AngleDegrees, o);

        if (o.CrossHatch) result.AddRange(GenerateAtAngle(contours, o.AngleDegrees + 90, o));

        return result;
    }

    private static List<Polyline> GenerateAtAngle(IReadOnlyList<Polyline> contours, double angleDegrees, HatchOptions o)
    {
        var spacing = Math.Max(0.01, o.SpacingMm);
        var toHatchSpace = Matrix2D.Rotate(-angleDegrees);
        var back = Matrix2D.Rotate(angleDegrees);

        // Only closed contours enclose an area worth filling.
        var closed = contours.Where(c => c.IsClosed && c.Count >= 3).Select(c => c.Transformed(toHatchSpace)).ToList();
        if (closed.Count == 0) return [];

        var bounds = Rect2.Empty;
        foreach (var c in closed) bounds = bounds.Union(c.Bounds);
        if (bounds.IsEmpty) return [];

        var edges = BuildEdges(closed);
        if (edges.Count == 0) return [];

        var lines = new List<Polyline>();
        var crossings = new List<double>(32);

        // Start half a step in so the first line is not exactly on the boundary,
        // where floating-point crossing tests are least reliable.
        var leftToRight = true;
        for (var y = bounds.MinY + spacing / 2; y < bounds.MaxY; y += spacing)
        {
            crossings.Clear();

            foreach (var e in edges)
            {
                // Half-open interval on Y: a vertex shared by two edges must count
                // once, not twice, or the fill leaks out of the shape.
                if (y < e.MinY || y >= e.MaxY) continue;
                var t = (y - e.Y0) / (e.Y1 - e.Y0);
                crossings.Add(e.X0 + t * (e.X1 - e.X0));
            }

            if (crossings.Count < 2) continue;
            crossings.Sort();

            var spans = new List<(double A, double B)>();
            for (var i = 0; i + 1 < crossings.Count; i += 2)
            {
                var a = crossings[i] + o.InsetMm;
                var b = crossings[i + 1] - o.InsetMm;
                if (b - a > spacing * 0.25) spans.Add((a, b));
            }

            if (spans.Count == 0) continue;
            if (!leftToRight) spans.Reverse();

            foreach (var (a, b) in spans)
            {
                var p = new Polyline(2);
                if (leftToRight)
                {
                    p.Add(back.Apply(a, y));
                    p.Add(back.Apply(b, y));
                }
                else
                {
                    p.Add(back.Apply(b, y));
                    p.Add(back.Apply(a, y));
                }
                lines.Add(p);
            }

            if (o.Bidirectional) leftToRight = !leftToRight;
        }

        return lines;
    }

    private readonly record struct Edge(double X0, double Y0, double X1, double Y1)
    {
        public double MinY => Math.Min(Y0, Y1);
        public double MaxY => Math.Max(Y0, Y1);
    }

    private static List<Edge> BuildEdges(IReadOnlyList<Polyline> contours)
    {
        var edges = new List<Edge>();
        foreach (var c in contours)
        {
            var n = c.Count;
            for (var i = 0; i < n; i++)
            {
                var a = c[i];
                var b = c[(i + 1) % n];
                // Horizontal edges contribute no crossing and cause double-counting.
                if (Math.Abs(a.Y - b.Y) < 1e-12) continue;
                edges.Add(new Edge(a.X, a.Y, b.X, b.Y));
            }
        }
        return edges;
    }

    /// <summary>
    /// Concentric inward offsets of the outline — the other way to fill a shape.
    /// Cheap approximation by shrinking each contour toward its centroid; adequate
    /// for simple convex artwork such as text and badges, which is where offset
    /// filling is actually wanted.
    /// </summary>
    public static List<Polyline> GenerateOffsetFill(IReadOnlyList<Polyline> contours, double spacingMm, int maxRings = 200)
    {
        var result = new List<Polyline>();

        foreach (var contour in contours.Where(c => c.IsClosed && c.Count >= 3))
        {
            var bounds = contour.Bounds;
            var centre = bounds.Center;
            var maxRadius = Math.Max(bounds.Width, bounds.Height) / 2;
            var rings = Math.Min(maxRings, (int)(maxRadius / Math.Max(0.01, spacingMm)));

            for (var ring = 1; ring <= rings; ring++)
            {
                var inset = ring * spacingMm;
                var shrunk = new Polyline(contour.Count) { IsClosed = true };
                var collapsed = false;

                foreach (var p in contour.Points)
                {
                    var toCentre = centre - p;
                    var distance = toCentre.Length;
                    if (distance <= inset + 1e-9)
                    {
                        collapsed = true;
                        break;
                    }
                    shrunk.Add(p + toCentre.Normalized * inset);
                }

                if (collapsed || shrunk.Count < 3) break;
                result.Add(shrunk);
            }
        }

        return result;
    }
}
