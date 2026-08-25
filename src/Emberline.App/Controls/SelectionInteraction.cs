using Avalonia;
using Emberline.Core.Documents;
using Emberline.Core.Geometry;

namespace Emberline.App.Controls;

public enum HandleKind
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
/// Hit testing and the maths behind dragging a selection.
///
/// Split out of the control so it can be reasoned about — and, more usefully, so
/// the awkward parts have somewhere to be explained. Handles are sized in *pixels*
/// and hit-tested in pixels, because a handle that shrinks as you zoom out becomes
/// impossible to grab; the transform they produce is in millimetres, because that
/// is what the document is in.
/// </summary>
public static class SelectionInteraction
{
    public const double HandleSize = 8;
    public const double HandleHitRadius = 9;

    /// <summary>Distance in pixels from the top edge to the rotate handle.</summary>
    public const double RotateHandleOffset = 26;

    /// <summary>Which handle, if any, is under a point. Screen space throughout.</summary>
    public static HandleKind HitTest(Rect selectionPixels, Point pointer, bool allowRotate = true)
    {
        if (selectionPixels.Width <= 0 && selectionPixels.Height <= 0) return HandleKind.None;

        // Rotate handle sits above the top edge, outside the box.
        if (allowRotate)
        {
            var rotate = new Point(selectionPixels.Center.X, selectionPixels.Top - RotateHandleOffset);
            if (Distance(pointer, rotate) <= HandleHitRadius + 2) return HandleKind.Rotate;
        }

        // Corners take priority over edges, which take priority over the interior.
        var candidates = new (HandleKind Kind, Point At)[]
        {
            (HandleKind.ScaleTopLeft, selectionPixels.TopLeft),
            (HandleKind.ScaleTopRight, selectionPixels.TopRight),
            (HandleKind.ScaleBottomRight, selectionPixels.BottomRight),
            (HandleKind.ScaleBottomLeft, selectionPixels.BottomLeft),
            (HandleKind.ScaleTop, new Point(selectionPixels.Center.X, selectionPixels.Top)),
            (HandleKind.ScaleBottom, new Point(selectionPixels.Center.X, selectionPixels.Bottom)),
            (HandleKind.ScaleLeft, new Point(selectionPixels.Left, selectionPixels.Center.Y)),
            (HandleKind.ScaleRight, new Point(selectionPixels.Right, selectionPixels.Center.Y)),
        };

        foreach (var (kind, at) in candidates)
        {
            if (Distance(pointer, at) <= HandleHitRadius) return kind;
        }

        return selectionPixels.Inflate(2).Contains(pointer) ? HandleKind.Move : HandleKind.None;
    }

    /// <summary>Handles map one-to-one onto the shared, testable enum in Core.</summary>
    public static SelectionHandle ToCore(HandleKind handle) => (SelectionHandle)(int)handle;

    public static Vec2 AnchorFor(HandleKind handle, Rect2 bounds) =>
        SelectionMath.AnchorFor(ToCore(handle), bounds);

    public static (double X, double Y) ScaleFactors(
        HandleKind handle,
        Rect2 bounds,
        Vec2 anchor,
        Vec2 current,
        bool uniform) =>
        SelectionMath.ScaleFactors(ToCore(handle), bounds, anchor, current, uniform);

    public static double AngleBetween(Vec2 pivot, Vec2 from, Vec2 to) => SelectionMath.AngleBetween(pivot, from, to);

    public static double SnapAngle(double degrees, double step = 15) => SelectionMath.SnapAngle(degrees, step);

    public static Vec2 SnapToGrid(Vec2 point, double gridMm) => SelectionMath.SnapToGrid(point, gridMm);

    /// <summary>The mouse cursor a handle should show.</summary>
    public static Avalonia.Input.StandardCursorType CursorFor(HandleKind handle) => handle switch
    {
        HandleKind.Move => Avalonia.Input.StandardCursorType.SizeAll,
        HandleKind.Rotate => Avalonia.Input.StandardCursorType.Hand,
        HandleKind.ScaleTopLeft or HandleKind.ScaleBottomRight => Avalonia.Input.StandardCursorType.TopLeftCorner,
        HandleKind.ScaleTopRight or HandleKind.ScaleBottomLeft => Avalonia.Input.StandardCursorType.TopRightCorner,
        HandleKind.ScaleLeft or HandleKind.ScaleRight => Avalonia.Input.StandardCursorType.SizeWestEast,
        HandleKind.ScaleTop or HandleKind.ScaleBottom => Avalonia.Input.StandardCursorType.SizeNorthSouth,
        _ => Avalonia.Input.StandardCursorType.Arrow,
    };

    private static double Distance(Point a, Point b) => Math.Sqrt(
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static List<Shape> InMarquee(IEnumerable<Shape> shapes, Rect2 marquee, bool requireFullyInside) =>
        SelectionMath.InMarquee(shapes, marquee, requireFullyInside);
}
