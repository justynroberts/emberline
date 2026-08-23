using OpenBurn.Core.Geometry;

namespace OpenBurn.Core.Documents;

/// <summary>
/// Vector geometry — imported SVG, traced bitmaps, primitives and text that has
/// been converted to outlines. Stored already flattened: OpenBurn deliberately
/// does not keep béziers alive past import, because every consumer downstream
/// (preview, CAM, framing, bounds) wants polylines and would flatten them anyway.
/// </summary>
public sealed class PathShape : Shape
{
    private readonly List<Polyline> _paths;

    public PathShape() => _paths = [];

    public PathShape(IEnumerable<Polyline> paths) => _paths = [.. paths];

    public IReadOnlyList<Polyline> Paths => _paths;

    private Rect2? _localBounds;

    public void Add(Polyline p)
    {
        _paths.Add(p);
        _localBounds = null;
    }

    public void AddRange(IEnumerable<Polyline> paths)
    {
        _paths.AddRange(paths);
        _localBounds = null;
    }

    /// <summary>
    /// Cached, because this walks every point of every path and the canvas asks
    /// for it on each pointer move to decide what is under the cursor. On a traced
    /// bitmap that is a quarter of a million points per mouse movement, which is
    /// felt as the drag lagging behind the pointer.
    ///
    /// Only the path list invalidates it. Moving, scaling and rotating a shape all
    /// change <see cref="Shape.Transform"/> rather than the points, and
    /// <see cref="Shape.Bounds"/> applies that transform to these bounds afterwards.
    /// </summary>
    public override Rect2 LocalBounds
    {
        get
        {
            if (_localBounds is { } cached) return cached;

            var r = Rect2.Empty;
            foreach (var p in _paths) r = r.Union(p.Bounds);
            _localBounds = r;
            return r;
        }
    }

    /// <summary>Recompute the bounds after editing the polylines in place.</summary>
    public void InvalidateBounds() => _localBounds = null;

    public override IReadOnlyList<Polyline> GetOutlines(double tolerance = Curves.DefaultTolerance)
    {
        var t = Transform;
        var result = new List<Polyline>(_paths.Count);
        foreach (var p in _paths) result.Add(p.Transformed(t));
        return result;
    }

    public override Shape Clone()
    {
        var copy = new PathShape(_paths.Select(p => p.Clone()));
        CopyBaseTo(copy);
        return copy;
    }

    /// <summary>Axis-aligned rectangle, optionally with rounded corners.</summary>
    public static PathShape Rectangle(double width, double height, double cornerRadius = 0)
    {
        var p = new Polyline { IsClosed = true };
        var r = Math.Min(cornerRadius, Math.Min(width, height) / 2);

        if (r <= 1e-9)
        {
            p.Add(0, 0);
            p.Add(width, 0);
            p.Add(width, height);
            p.Add(0, height);
        }
        else
        {
            const double q = Math.PI / 2;
            Curves.FlattenArc(p, new Vec2(width - r, r), r, -q, q);
            Curves.FlattenArc(p, new Vec2(width - r, height - r), r, 0, q);
            Curves.FlattenArc(p, new Vec2(r, height - r), r, q, q);
            Curves.FlattenArc(p, new Vec2(r, r), r, Math.PI, q);
        }

        return new PathShape([p]) { Name = "Rectangle" };
    }

    public static PathShape Ellipse(double rx, double ry, double tolerance = Curves.DefaultTolerance)
    {
        var p = new Polyline { IsClosed = true };
        var rMax = Math.Max(rx, ry);
        var maxStep = 2 * Math.Acos(1 - Math.Clamp(tolerance / Math.Max(rMax, 1e-6), 0, 1));
        if (double.IsNaN(maxStep) || maxStep <= 1e-6) maxStep = 0.1;
        var steps = Math.Max(12, (int)Math.Ceiling(2 * Math.PI / maxStep));

        for (var i = 0; i < steps; i++)
        {
            var a = 2 * Math.PI * i / steps;
            var (s, c) = Math.SinCos(a);
            p.Add(rx * c, ry * s);
        }
        return new PathShape([p]) { Name = rx == ry ? "Circle" : "Ellipse" };
    }

    public static PathShape Polygon(int sides, double radius, double rotationDeg = 0)
    {
        sides = Math.Max(3, sides);
        var p = new Polyline(sides) { IsClosed = true };
        var offset = rotationDeg * Math.PI / 180;
        for (var i = 0; i < sides; i++)
        {
            var a = offset + 2 * Math.PI * i / sides;
            var (s, c) = Math.SinCos(a);
            p.Add(radius * c, radius * s);
        }
        return new PathShape([p]) { Name = $"{sides}-gon" };
    }

    public static PathShape Line(Vec2 from, Vec2 to) =>
        new([new Polyline([from, to])]) { Name = "Line" };
}
