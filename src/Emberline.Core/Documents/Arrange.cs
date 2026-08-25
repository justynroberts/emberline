using Emberline.Core.Geometry;

namespace Emberline.Core.Documents;

public enum AlignEdge { Left, HorizontalCentre, Right, Top, VerticalCentre, Bottom }

public enum DistributeAxis { Horizontal, Vertical }

/// <summary>
/// Align, distribute and array.
///
/// All of it operates on bounding boxes rather than geometry, which is what people
/// expect: aligning a circle and a square by their left edges should line up the
/// leftmost point of each, not their centres of mass.
/// </summary>
public static class Arrange
{
    public static void Align(IReadOnlyList<Shape> shapes, AlignEdge edge, Rect2? relativeTo = null)
    {
        var movable = shapes.Where(s => !s.Locked && !s.Bounds.IsEmpty).ToList();
        if (movable.Count == 0) return;

        // With one shape selected, aligning is only meaningful against something
        // else — the bed. With several, they align to each other.
        var target = relativeTo ?? Bounds(movable);
        if (target.IsEmpty) return;

        foreach (var shape in movable)
        {
            var b = shape.Bounds;
            var delta = edge switch
            {
                AlignEdge.Left => new Vec2(target.MinX - b.MinX, 0),
                AlignEdge.Right => new Vec2(target.MaxX - b.MaxX, 0),
                AlignEdge.HorizontalCentre => new Vec2(target.Center.X - b.Center.X, 0),
                AlignEdge.Bottom => new Vec2(0, target.MinY - b.MinY),
                AlignEdge.Top => new Vec2(0, target.MaxY - b.MaxY),
                AlignEdge.VerticalCentre => new Vec2(0, target.Center.Y - b.Center.Y),
                _ => Vec2.Zero,
            };

            if (delta.LengthSquared > 1e-18) shape.Translate(delta);
        }
    }

    /// <summary>Even the gaps between shapes, leaving the outermost two where they are.</summary>
    public static void Distribute(IReadOnlyList<Shape> shapes, DistributeAxis axis)
    {
        var movable = shapes.Where(s => !s.Locked && !s.Bounds.IsEmpty).ToList();
        if (movable.Count < 3) return;

        var ordered = axis == DistributeAxis.Horizontal
            ? movable.OrderBy(s => s.Bounds.MinX).ToList()
            : movable.OrderBy(s => s.Bounds.MinY).ToList();

        var first = ordered[0].Bounds;
        var last = ordered[^1].Bounds;

        var span = axis == DistributeAxis.Horizontal
            ? last.MaxX - first.MinX
            : last.MaxY - first.MinY;

        var occupied = ordered.Sum(s => axis == DistributeAxis.Horizontal ? s.Bounds.Width : s.Bounds.Height);
        var gap = (span - occupied) / (ordered.Count - 1);

        var cursor = axis == DistributeAxis.Horizontal ? first.MinX : first.MinY;

        foreach (var shape in ordered)
        {
            var b = shape.Bounds;
            if (axis == DistributeAxis.Horizontal)
            {
                shape.Translate(new Vec2(cursor - b.MinX, 0));
                cursor += b.Width + gap;
            }
            else
            {
                shape.Translate(new Vec2(0, cursor - b.MinY));
                cursor += b.Height + gap;
            }
        }
    }

    /// <summary>
    /// Duplicate into a grid. The batch-production case from the PRD: one keyring
    /// becomes twenty-four, spaced to fit the bed.
    /// </summary>
    public static List<Shape> Array(
        Shape source,
        int columns,
        int rows,
        double spacingXMm,
        double spacingYMm)
    {
        var result = new List<Shape>();
        var bounds = source.Bounds;
        if (bounds.IsEmpty) return result;

        var stepX = bounds.Width + spacingXMm;
        var stepY = bounds.Height + spacingYMm;

        for (var row = 0; row < Math.Max(1, rows); row++)
        {
            for (var column = 0; column < Math.Max(1, columns); column++)
            {
                if (row == 0 && column == 0) continue; // the original stays put

                var copy = source.Clone();
                copy.Translate(new Vec2(column * stepX, row * stepY));
                result.Add(copy);
            }
        }

        return result;
    }

    /// <summary>Copy a shape onto each detected workpiece, centred on it.</summary>
    public static List<Shape> PlaceOnEach(Shape source, IReadOnlyList<Vec2> centres)
    {
        var result = new List<Shape>();
        var bounds = source.Bounds;
        if (bounds.IsEmpty || centres.Count == 0) return result;

        foreach (var centre in centres)
        {
            var copy = source.Clone();
            copy.MoveTo(new Vec2(centre.X - bounds.Width / 2, centre.Y - bounds.Height / 2));
            result.Add(copy);
        }

        return result;
    }

    public static Rect2 Bounds(IEnumerable<Shape> shapes)
    {
        var r = Rect2.Empty;
        foreach (var s in shapes) r = r.Union(s.Bounds);
        return r;
    }

    /// <summary>
    /// Scale a selection as a unit about its own bounding box, so relative
    /// positions are preserved rather than each shape scaling in place.
    /// </summary>
    public static void ScaleSelection(IReadOnlyList<Shape> shapes, double scaleX, double scaleY, Vec2 anchor)
    {
        foreach (var shape in shapes)
        {
            if (shape.Locked) continue;
            shape.ScaleAbout(scaleX, scaleY, anchor);
        }
    }

    public static void RotateSelection(IReadOnlyList<Shape> shapes, double degrees, Vec2 pivot)
    {
        foreach (var shape in shapes)
        {
            if (shape.Locked) continue;
            shape.RotateAbout(degrees, pivot);
        }
    }
}
