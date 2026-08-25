using Emberline.Core.Geometry;

namespace Emberline.Core.Documents;

/// <summary>Which part of a selection box a drag has hold of.</summary>
public enum SelectionHandle
{
    None,
    Move,
    ScaleTopLeft,
    ScaleTop,
    ScaleTopRight,
    ScaleRight,
    ScaleBottomRight,
    ScaleBottom,
    ScaleBottomLeft,
    ScaleLeft,
    Rotate,
}

/// <summary>
/// The maths behind dragging a selection.
///
/// Kept out of the view so it can be tested without a window. Everything here is
/// in bed millimetres; the view is responsible for hit testing in pixels, because
/// a handle that shrinks with zoom is impossible to grab.
/// </summary>
public static class SelectionMath
{
    /// <summary>The point a scale should pivot about — the corner opposite the one being dragged.</summary>
    public static Vec2 AnchorFor(SelectionHandle handle, Rect2 bounds) => handle switch
    {
        SelectionHandle.ScaleTopLeft => new Vec2(bounds.MaxX, bounds.MinY),
        SelectionHandle.ScaleTop => new Vec2(bounds.Center.X, bounds.MinY),
        SelectionHandle.ScaleTopRight => new Vec2(bounds.MinX, bounds.MinY),
        SelectionHandle.ScaleRight => new Vec2(bounds.MinX, bounds.Center.Y),
        SelectionHandle.ScaleBottomRight => new Vec2(bounds.MinX, bounds.MaxY),
        SelectionHandle.ScaleBottom => new Vec2(bounds.Center.X, bounds.MaxY),
        SelectionHandle.ScaleBottomLeft => new Vec2(bounds.MaxX, bounds.MaxY),
        SelectionHandle.ScaleLeft => new Vec2(bounds.MaxX, bounds.Center.Y),
        _ => bounds.Center,
    };

    public static bool IsHorizontal(SelectionHandle handle) =>
        handle is SelectionHandle.ScaleLeft or SelectionHandle.ScaleRight
            or SelectionHandle.ScaleTopLeft or SelectionHandle.ScaleTopRight
            or SelectionHandle.ScaleBottomLeft or SelectionHandle.ScaleBottomRight;

    public static bool IsVertical(SelectionHandle handle) =>
        handle is SelectionHandle.ScaleTop or SelectionHandle.ScaleBottom
            or SelectionHandle.ScaleTopLeft or SelectionHandle.ScaleTopRight
            or SelectionHandle.ScaleBottomLeft or SelectionHandle.ScaleBottomRight;

    /// <summary>Smallest scale a drag may produce. Below this the geometry collapses and cannot be recovered by eye.</summary>
    public const double MinimumScale = 0.01;

    /// <summary>
    /// Scale factors for dragging <paramref name="handle"/> to <paramref name="current"/>.
    ///
    /// Edge handles constrain to one axis. Uniform locks the ratio, taking the
    /// larger factor so the shape follows the pointer rather than lagging behind
    /// it on the unconstrained axis.
    /// </summary>
    public static (double X, double Y) ScaleFactors(
        SelectionHandle handle,
        Rect2 bounds,
        Vec2 anchor,
        Vec2 current,
        bool uniform)
    {
        var horizontal = IsHorizontal(handle);
        var vertical = IsVertical(handle);

        var scaleX = 1.0;
        var scaleY = 1.0;

        if (horizontal && Math.Abs(bounds.Width) > 1e-9)
        {
            scaleX = Math.Abs(current.X - anchor.X) / bounds.Width;
        }

        if (vertical && Math.Abs(bounds.Height) > 1e-9)
        {
            scaleY = Math.Abs(current.Y - anchor.Y) / bounds.Height;
        }

        if (uniform && horizontal && vertical)
        {
            var s = Math.Max(scaleX, scaleY);
            scaleX = s;
            scaleY = s;
        }

        if (scaleX < MinimumScale) scaleX = MinimumScale;
        if (scaleY < MinimumScale) scaleY = MinimumScale;

        return (horizontal ? scaleX : 1.0, vertical ? scaleY : 1.0);
    }

    /// <summary>Signed angle in degrees swept from <paramref name="from"/> to <paramref name="to"/> about a pivot.</summary>
    public static double AngleBetween(Vec2 pivot, Vec2 from, Vec2 to)
    {
        var a = Math.Atan2(from.Y - pivot.Y, from.X - pivot.X);
        var b = Math.Atan2(to.Y - pivot.Y, to.X - pivot.X);
        return (b - a) * 180 / Math.PI;
    }

    public static double SnapAngle(double degrees, double step = 15) =>
        step <= 0 ? degrees : Math.Round(degrees / step) * step;

    public static Vec2 SnapToGrid(Vec2 point, double gridMm) =>
        gridMm <= 0 ? point : new Vec2(Math.Round(point.X / gridMm) * gridMm, Math.Round(point.Y / gridMm) * gridMm);

    /// <summary>
    /// Shapes caught by a marquee.
    ///
    /// Dragging left-to-right selects only what is fully enclosed; right-to-left
    /// catches anything touched. That is the convention every CAD package uses and
    /// people reach for it without thinking.
    /// </summary>
    public static List<Shape> InMarquee(IEnumerable<Shape> shapes, Rect2 marquee, bool requireFullyInside)
    {
        var result = new List<Shape>();
        foreach (var shape in shapes)
        {
            if (!shape.Visible || shape.Locked) continue;

            var b = shape.Bounds;
            if (b.IsEmpty) continue;

            if (requireFullyInside ? marquee.Contains(b) : marquee.Intersects(b)) result.Add(shape);
        }
        return result;
    }
}
