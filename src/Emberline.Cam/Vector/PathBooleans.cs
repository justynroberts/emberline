using Emberline.Core.Geometry;
using SkiaSharp;

namespace Emberline.Cam.Vector;

/// <summary>
/// Combining overlapping outlines into the shape they actually make.
///
/// Cutting a list of closed paths one at a time is wrong whenever any of them
/// overlap. Two overlapping circles cut individually are two full circles, so the
/// beam runs straight through the middle of the finished piece and cuts it in
/// half. What is wanted is the boundary of the union — the outline you would draw
/// round the pair — with the shared interior left alone.
///
/// Holes are the other half of the same problem and must survive: the counter of
/// an "o", the middle of a washer. A union is not "the outermost contour", it is
/// every boundary between inside and outside.
///
/// The arithmetic is Skia's. Robust polygon booleans are a well-known source of
/// subtle failure around touching edges and coincident vertices, and Skia's
/// implementation is far better tested than anything written here would be.
/// </summary>
public static class PathBooleans
{
    /// <summary>
    /// Merge overlapping closed paths into their true outlines.
    ///
    /// Open paths cannot bound an area, so they are passed through untouched — a
    /// score line across a shape is a line, not a region, and merging it into
    /// anything would be meaningless.
    /// </summary>
    public static List<Polyline> Union(IReadOnlyList<Polyline> paths, double toleranceMm = 0.02)
    {
        var closed = new List<Polyline>();
        var open = new List<Polyline>();

        foreach (var path in paths)
        {
            if (path.Count < 3) { open.Add(path); continue; }
            (path.IsClosed ? closed : open).Add(path);
        }

        // Nothing to combine.
        if (closed.Count < 2) return [.. paths];

        // Two cases that look alike and mean opposite things.
        //
        // Shapes that partially overlap must be merged: the boundary of the pair
        // is what gets cut, and the shared interior is left alone. Shapes where
        // one contains the other must not be merged: that is a hole — a letter
        // counter, the middle of a washer — and merging it fills it in.
        //
        // Neither fill rule can tell them apart on its own. Even-odd turns an
        // overlap into a hole, which is exactly wrong; winding fills a counter in,
        // which is exactly wrong the other way. So the overlaps are merged first,
        // explicitly, and only then is even-odd used to turn what is left —
        // genuine containment — into holes.
        var parts = closed.Select(ToSkiaSingle).ToList();

        try
        {
            MergeOverlapping(parts);

            using var all = Combine(parts);
            using var simplified = all.Simplify() ?? all;

            var result = FromSkia(simplified, toleranceMm);

            // If the boolean produced nothing usable, the original paths are a
            // better answer than none: refusing to cut is worse than cutting what
            // was drawn.
            if (result.Count == 0) return [.. paths];

            result.AddRange(open);
            return result;
        }
        finally
        {
            foreach (var part in parts) part.Dispose();
        }
    }

    /// <summary>
    /// Repeatedly union any two paths whose boundaries actually cross, until none
    /// do. Containment is deliberately left alone.
    /// </summary>
    private static void MergeOverlapping(List<SKPath> parts)
    {
        var merged = true;
        var guard = 0;

        while (merged && guard++ < 200)
        {
            merged = false;

            for (var i = 0; i < parts.Count && !merged; i++)
            {
                for (var j = i + 1; j < parts.Count && !merged; j++)
                {
                    if (!PartiallyOverlap(parts[i], parts[j])) continue;

                    var union = parts[i].Op(parts[j], SKPathOp.Union);
                    if (union is null) continue;

                    parts[i].Dispose();
                    parts[j].Dispose();
                    parts.RemoveAt(j);
                    parts[i] = union;
                    merged = true;
                }
            }
        }
    }

    /// <summary>
    /// True when two shapes cross: they share area, and each has area the other
    /// does not. Containment fails the second test, which is the whole point.
    /// </summary>
    private static bool PartiallyOverlap(SKPath a, SKPath b)
    {
        if (!a.Bounds.IntersectsWith(b.Bounds)) return false;

        using var shared = a.Op(b, SKPathOp.Intersect);
        if (shared is null || shared.IsEmpty) return false;

        using var aOnly = a.Op(b, SKPathOp.Difference);
        using var bOnly = b.Op(a, SKPathOp.Difference);

        return aOnly is { IsEmpty: false } && bOnly is { IsEmpty: false };
    }

    private static SKPath Combine(List<SKPath> parts)
    {
        var builder = new SKPathBuilder { FillType = SKPathFillType.EvenOdd };
        foreach (var part in parts) builder.AddPath(part);
        return builder.Detach();
    }

    private static SKPath ToSkiaSingle(Polyline path)
    {
        var builder = new SKPathBuilder();
        builder.MoveTo((float)path[0].X, (float)path[0].Y);
        for (var i = 1; i < path.Count; i++) builder.LineTo((float)path[i].X, (float)path[i].Y);
        builder.Close();
        return builder.Detach();
    }

    /// <summary>Do any of these closed paths overlap? Used to avoid the work when none do.</summary>
    public static bool AnyOverlap(IReadOnlyList<Polyline> paths)
    {
        var closed = paths.Where(p => p.IsClosed && p.Count >= 3).ToList();

        for (var i = 0; i < closed.Count; i++)
        {
            for (var j = i + 1; j < closed.Count; j++)
            {
                if (closed[i].Bounds.Intersects(closed[j].Bounds)) return true;
            }
        }
        return false;
    }

    private static List<Polyline> FromSkia(SKPath path, double toleranceMm)
    {
        var result = new List<Polyline>();
        Polyline? current = null;

        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];

        while (true)
        {
            var verb = iterator.Next(points);
            if (verb == SKPathVerb.Done) break;

            switch (verb)
            {
                case SKPathVerb.Move:
                    Flush(result, current, toleranceMm);
                    current = new Polyline { IsClosed = true };
                    current.Add(points[0].X, points[0].Y);
                    break;

                case SKPathVerb.Line:
                    current?.Add(points[1].X, points[1].Y);
                    break;

                case SKPathVerb.Close:
                    Flush(result, current, toleranceMm);
                    current = null;
                    break;
            }
        }

        Flush(result, current, toleranceMm);
        return result;
    }

    private static void Flush(List<Polyline> into, Polyline? path, double toleranceMm)
    {
        if (path is null || path.Count < 3) return;

        // Skia closes contours by returning to the start point; carrying the
        // duplicate through would leave a zero-length move in the G-code.
        if ((path.First - path.Last).Length < 1e-6 && path.Count > 3)
        {
            var trimmed = new Polyline(path.Points.Take(path.Count - 1), closed: true);
            path = trimmed;
        }

        into.Add(toleranceMm > 0 ? PathOps.Simplify(path, toleranceMm) : path);
    }
}
