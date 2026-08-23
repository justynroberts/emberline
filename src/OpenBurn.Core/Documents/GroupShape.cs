using OpenBurn.Core.Geometry;

namespace OpenBurn.Core.Documents;

/// <summary>A transform applied to a set of children. SVG &lt;g&gt; maps straight onto this.</summary>
public sealed class GroupShape : Shape
{
    private readonly List<Shape> _children;

    public GroupShape() => _children = [];
    public GroupShape(IEnumerable<Shape> children) => _children = [.. children];

    public IReadOnlyList<Shape> Children => _children;

    public void Add(Shape s) => _children.Add(s);
    public bool Remove(Shape s) => _children.Remove(s);

    public override Rect2 LocalBounds
    {
        get
        {
            var r = Rect2.Empty;
            foreach (var c in _children) r = r.Union(c.Bounds);
            return r;
        }
    }

    public override IReadOnlyList<Polyline> GetOutlines(double tolerance = Curves.DefaultTolerance)
    {
        var t = Transform;
        var result = new List<Polyline>();
        foreach (var c in _children)
        {
            if (!c.Visible) continue;
            foreach (var p in c.GetOutlines(tolerance)) result.Add(p.Transformed(t));
        }
        return result;
    }

    /// <summary>Flatten the group away, baking its transform into the children.</summary>
    public List<Shape> Ungroup()
    {
        var t = Transform;
        foreach (var c in _children) c.Transform = t * c.Transform;
        var result = new List<Shape>(_children);
        _children.Clear();
        return result;
    }

    public override Shape Clone()
    {
        var copy = new GroupShape(_children.Select(c => c.Clone()));
        CopyBaseTo(copy);
        return copy;
    }
}
