namespace OpenBurn.Core.Geometry;

/// <summary>Simplification, smoothing and travel-order optimisation for flattened paths.</summary>
public static class PathOps
{
    /// <summary>
    /// Douglas–Peucker, iterative. A traced photograph can produce contours with
    /// hundreds of thousands of points; recursion blows the stack on those and the
    /// crash only shows up on the one image the user cares about.
    /// </summary>
    public static Polyline Simplify(Polyline input, double tolerance)
    {
        var n = input.Count;
        if (n < 3 || tolerance <= 0) return input.Clone();

        var keep = new bool[n];
        keep[0] = true;
        keep[n - 1] = true;
        var tol2 = tolerance * tolerance;

        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, n - 1));

        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            if (last <= first + 1) continue;

            var a = input[first];
            var b = input[last];
            var maxD = -1.0;
            var index = -1;

            for (var i = first + 1; i < last; i++)
            {
                var d = Curves.DistanceToLineSquared(input[i], a, b);
                if (d > maxD) { maxD = d; index = i; }
            }

            if (maxD > tol2 && index > 0)
            {
                keep[index] = true;
                stack.Push((first, index));
                stack.Push((index, last));
            }
        }

        var result = new Polyline(n) { IsClosed = input.IsClosed };
        for (var i = 0; i < n; i++)
        {
            if (keep[i]) result.Add(input[i]);
        }
        return result;
    }

    /// <summary>Chaikin corner cutting. Cheap way to take the stair-steps off a traced bitmap.</summary>
    public static Polyline Smooth(Polyline input, int iterations = 1)
    {
        var current = input;
        for (var it = 0; it < iterations; it++)
        {
            var n = current.Count;
            if (n < 3) break;

            var next = new Polyline(n * 2) { IsClosed = current.IsClosed };
            if (!current.IsClosed) next.Add(current[0]);

            var last = current.IsClosed ? n : n - 1;
            for (var i = 0; i < last; i++)
            {
                var p0 = current[i];
                var p1 = current[(i + 1) % n];
                next.Add(Vec2.Lerp(p0, p1, 0.25));
                next.Add(Vec2.Lerp(p0, p1, 0.75));
            }

            if (!current.IsClosed) next.Add(current[^1]);
            current = next;
        }
        return current;
    }

    /// <summary>
    /// Order paths to minimise rapid travel. Nearest-neighbour seed, then a bounded
    /// 2-opt improvement. Open paths may be reversed; closed paths may be re-started
    /// at their nearest vertex, which on traced artwork saves more travel than the
    /// re-ordering does.
    /// </summary>
    public static List<Polyline> OptimiseTravel(IReadOnlyList<Polyline> paths, Vec2 start, int twoOptRounds = 2)
    {
        if (paths.Count <= 1) return [.. paths];

        var remaining = new List<Polyline>(paths);
        var ordered = new List<Polyline>(paths.Count);
        var cursor = start;

        while (remaining.Count > 0)
        {
            var bestIndex = 0;
            var bestDistance = double.MaxValue;
            var bestReversed = false;

            for (var i = 0; i < remaining.Count; i++)
            {
                var p = remaining[i];
                if (p.Count == 0) continue;

                var dStart = p.First.DistanceSquaredTo(cursor);
                if (dStart < bestDistance)
                {
                    bestDistance = dStart;
                    bestIndex = i;
                    bestReversed = false;
                }

                if (!p.IsClosed)
                {
                    var dEnd = p.Last.DistanceSquaredTo(cursor);
                    if (dEnd < bestDistance)
                    {
                        bestDistance = dEnd;
                        bestIndex = i;
                        bestReversed = true;
                    }
                }
            }

            var chosen = remaining[bestIndex];
            remaining.RemoveAt(bestIndex);

            if (chosen.IsClosed) chosen = chosen.RotatedToNearest(cursor);
            else if (bestReversed) chosen = chosen.Reversed();

            ordered.Add(chosen);
            cursor = chosen.ExitPoint;
        }

        return twoOptRounds > 0 ? TwoOpt(ordered, start, twoOptRounds) : ordered;
    }

    private static List<Polyline> TwoOpt(List<Polyline> order, Vec2 start, int rounds)
    {
        // The full O(n²) sweep is not worth the wait past a few hundred paths, and
        // the nearest-neighbour seed is already close to optimal at that size.
        var limit = Math.Min(order.Count, 400);
        var bestCost = TravelCost(order, start);

        for (var round = 0; round < rounds; round++)
        {
            var improved = false;
            for (var i = 0; i < limit - 1; i++)
            {
                for (var j = i + 1; j < limit; j++)
                {
                    var candidate = new List<Polyline>(order);
                    candidate.Reverse(i, j - i + 1);
                    for (var k = i; k <= j; k++)
                    {
                        if (!candidate[k].IsClosed) candidate[k] = candidate[k].Reversed();
                    }

                    var cost = TravelCost(candidate, start);
                    if (cost < bestCost - 1e-9)
                    {
                        order = candidate;
                        bestCost = cost;
                        improved = true;
                    }
                }
            }
            if (!improved) break;
        }
        return order;
    }

    public static double TravelCost(IReadOnlyList<Polyline> order, Vec2 start)
    {
        var cursor = start;
        double total = 0;
        foreach (var p in order)
        {
            if (p.Count == 0) continue;
            total += cursor.DistanceTo(p.First);
            cursor = p.ExitPoint;
        }
        return total;
    }

    /// <summary>
    /// Sort closed contours so that anything nested inside another shape is cut
    /// first. Cut the outside of a ring before the inside and the part drops out
    /// mid-job, then the head ploughs into it.
    /// </summary>
    public static List<Polyline> InsideOutFirst(IReadOnlyList<Polyline> paths)
    {
        var depths = new int[paths.Count];
        for (var i = 0; i < paths.Count; i++)
        {
            if (!paths[i].IsClosed || paths[i].Count < 3) continue;
            var probe = paths[i].First;
            for (var j = 0; j < paths.Count; j++)
            {
                if (i == j || !paths[j].IsClosed || paths[j].Count < 3) continue;
                if (paths[j].Contains(probe)) depths[i]++;
            }
        }

        return [.. paths
            .Select((p, i) => (Path: p, Depth: depths[i], Index: i))
            .OrderByDescending(t => t.Depth)
            .ThenBy(t => t.Index)
            .Select(t => t.Path)];
    }

    /// <summary>Convex hull (monotone chain). Used by the "hull" framing mode.</summary>
    public static Polyline ConvexHull(IEnumerable<Vec2> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3) return new Polyline(pts, closed: pts.Count > 1);

        static double Cross(Vec2 o, Vec2 a, Vec2 b) => (a - o).Cross(b - o);

        var hull = new List<Vec2>(pts.Count * 2);
        foreach (var p in pts)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }

        var lower = hull.Count + 1;
        for (var i = pts.Count - 2; i >= 0; i--)
        {
            var p = pts[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }

        hull.RemoveAt(hull.Count - 1);
        return new Polyline(hull, closed: true);
    }
}
