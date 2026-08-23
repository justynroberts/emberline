namespace OpenBurn.Core.Geometry;

/// <summary>
/// A flattened chain of points in millimetres. Every curve in OpenBurn becomes
/// one of these before CAM sees it — the CAM layer never deals with béziers,
/// which keeps toolpath generation simple and testable.
/// </summary>
public sealed class Polyline
{
    private readonly List<Vec2> _points;

    public Polyline() => _points = [];
    public Polyline(int capacity) => _points = new List<Vec2>(capacity);
    public Polyline(IEnumerable<Vec2> points, bool closed = false)
    {
        _points = [.. points];
        IsClosed = closed;
    }

    public IReadOnlyList<Vec2> Points => _points;
    public int Count => _points.Count;
    public bool IsClosed { get; set; }

    public Vec2 this[int i] => _points[i];
    public Vec2 First => _points[0];
    public Vec2 Last => _points[^1];

    /// <summary>The point the head leaves from — for a closed loop that is the start again.</summary>
    public Vec2 ExitPoint => IsClosed ? _points[0] : _points[^1];

    public void Add(Vec2 p)
    {
        // Collapse duplicate consecutive points; they produce zero-length G1 moves
        // that GRBL's planner has to chew through for nothing.
        if (_points.Count > 0 && _points[^1].DistanceSquaredTo(p) < 1e-14) return;
        _points.Add(p);
    }

    public void Add(double x, double y) => Add(new Vec2(x, y));

    public void AddRange(IEnumerable<Vec2> points)
    {
        foreach (var p in points) Add(p);
    }

    public Rect2 Bounds => Rect2.FromPoints(_points);

    public double Length
    {
        get
        {
            double total = 0;
            for (var i = 1; i < _points.Count; i++) total += _points[i - 1].DistanceTo(_points[i]);
            if (IsClosed && _points.Count > 2) total += _points[^1].DistanceTo(_points[0]);
            return total;
        }
    }

    /// <summary>Signed area. Positive is counter-clockwise in a Y-up frame.</summary>
    public double SignedArea
    {
        get
        {
            if (_points.Count < 3) return 0;
            double a = 0;
            for (var i = 0; i < _points.Count; i++)
            {
                var j = (i + 1) % _points.Count;
                a += _points[i].X * _points[j].Y - _points[j].X * _points[i].Y;
            }
            return a / 2;
        }
    }

    public bool IsClockwise => SignedArea < 0;

    public Polyline Reversed()
    {
        var r = new Polyline(_points.Count) { IsClosed = IsClosed };
        for (var i = _points.Count - 1; i >= 0; i--) r._points.Add(_points[i]);
        return r;
    }

    public Polyline Transformed(Matrix2D m)
    {
        var r = new Polyline(_points.Count) { IsClosed = IsClosed };
        foreach (var p in _points) r._points.Add(m.Apply(p));
        return r;
    }

    /// <summary>Even-odd containment test. Used to decide cut ordering for nested shapes.</summary>
    public bool Contains(Vec2 p)
    {
        var inside = false;
        for (int i = 0, j = _points.Count - 1; i < _points.Count; j = i++)
        {
            var pi = _points[i];
            var pj = _points[j];
            if (pi.Y > p.Y != pj.Y > p.Y &&
                p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>Re-start a closed loop at the vertex nearest <paramref name="target"/>, which cuts travel.</summary>
    public Polyline RotatedToNearest(Vec2 target)
    {
        if (!IsClosed || _points.Count < 3) return this;
        var best = 0;
        var bestD = double.MaxValue;
        for (var i = 0; i < _points.Count; i++)
        {
            var d = _points[i].DistanceSquaredTo(target);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best == 0) return this;
        var r = new Polyline(_points.Count) { IsClosed = true };
        for (var i = 0; i < _points.Count; i++) r._points.Add(_points[(best + i) % _points.Count]);
        return r;
    }

    public Polyline Clone() => new(_points, IsClosed);
}
