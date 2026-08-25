using Emberline.Cam.Raster;
using Emberline.Core.Documents;
using Emberline.Core.Geometry;

namespace Emberline.Cam.Trace;

/// <summary>How a bitmap is turned into paths.</summary>
public enum TraceMode
{
    /// <summary>Follow the boundary of every dark region. Shapes come out as closed outlines.</summary>
    Outline,

    /// <summary>
    /// Thin every dark region to a single-pixel spine and follow that. Open paths.
    /// This is what line art, signatures and hand lettering want: outline mode draws
    /// both sides of every pen stroke, which on a laser burns each line twice.
    /// </summary>
    Centreline,
}

/// <remarks>
/// Name clash worth knowing about: <c>System.Diagnostics</c> has its own
/// <c>TraceOptions</c>, so a file that needs both wants a using alias.
/// </remarks>
public sealed record TraceOptions
{
    /// <summary>Pixels darker than this are treated as ink. 0–255.</summary>
    public int Threshold { get; init; } = 128;

    /// <summary>Outline the regions, or reduce them to a centreline.</summary>
    public TraceMode Mode { get; init; } = TraceMode.Outline;

    /// <summary>Douglas–Peucker tolerance in pixels. Higher gives fewer, straighter segments.</summary>
    public double SimplifyTolerancePx { get; init; } = 0.8;

    /// <summary>Chaikin smoothing passes. Two takes the stair-steps off without losing corners.</summary>
    public int SmoothPasses { get; init; } = 2;

    /// <summary>Discard contours enclosing fewer pixels than this — speckle, not artwork.</summary>
    public int MinimumAreaPx { get; init; } = 12;

    /// <summary>Discard centreline paths shorter than this. Thinning always leaves a few stubs.</summary>
    public double MinimumLengthPx { get; init; } = 4;

    /// <summary>Remove isolated pixels before tracing.</summary>
    public bool Despeckle { get; init; } = true;

    /// <summary>Trace light regions instead of dark ones.</summary>
    public bool Invert { get; init; }

    /// <summary>
    /// Resample anything larger than this before tracing. Two megapixels is far
    /// more detail than a diode laser can resolve, and tracing at full camera
    /// resolution mostly buys noise.
    /// </summary>
    public int MaxWorkingPixels { get; init; } = 2_000_000;

    /// <summary>
    /// Stop once the trace has produced this many points. A grainy photograph can
    /// generate millions, which is not a job anybody can run — better to stop and
    /// say so than to hand back something that locks up the preview.
    /// </summary>
    public int MaxPoints { get; init; } = 250_000;

    public static readonly TraceOptions Default = new();
}

public sealed record TraceResult(
    IReadOnlyList<Polyline> Contours,
    int PixelsTraced,
    TraceMode Mode = TraceMode.Outline,
    IReadOnlyList<string>? Warnings = null)
{
    public int ContourCount => Contours.Count;

    /// <summary>What the trace had to do to the image, or gave up on. Worth showing.</summary>
    public IReadOnlyList<string> Notes => Warnings ?? [];

    public int PointCount
    {
        get
        {
            var total = 0;
            foreach (var c in Contours) total += c.Count;
            return total;
        }
    }

    /// <summary>Total path length in pixels — a rough proxy for how long the burn will take.</summary>
    public double TotalLengthPx
    {
        get
        {
            var total = 0.0;
            foreach (var c in Contours) total += c.Length;
            return total;
        }
    }
}

/// <summary>
/// Bitmap to vector contours.
///
/// Marching squares over a binarised image, then simplification and smoothing.
/// This is not a curve-fitting tracer like Potrace — it produces polylines, not
/// béziers — which for a laser is the right trade: everything downstream flattens
/// curves anyway, and marching squares is exact about topology, so holes come out
/// as holes rather than as filled blobs.
/// </summary>
public static class BitmapTracer
{
    public static TraceResult Trace(RasterImage source, TraceOptions? options = null)
    {
        var o = options ?? TraceOptions.Default;
        var warnings = new List<string>();

        // Work at a sane resolution, then scale the paths back up. Doing this
        // first also takes the edge off sensor noise, which is the main thing
        // that turns a photograph into a hundred thousand unusable contours.
        var working = source;
        var backToSource = Matrix2D.Identity;

        if (o.MaxWorkingPixels > 0 && (long)source.Width * source.Height > o.MaxWorkingPixels)
        {
            var scale = Math.Sqrt(o.MaxWorkingPixels / ((double)source.Width * source.Height));
            var w = Math.Max(1, (int)Math.Round(source.Width * scale));
            var h = Math.Max(1, (int)Math.Round(source.Height * scale));

            working = ImageProcessor.Resample(source, w, h);
            backToSource = Matrix2D.Scale(source.Width / (double)w, source.Height / (double)h);
            warnings.Add($"Traced at {w}×{h} rather than {source.Width}×{source.Height}. " +
                         "Beyond about two megapixels the extra detail is finer than the beam.");
        }

        var (grid, gw, gh) = Binarise(working, o);

        var pixelsTraced = 0;
        for (var i = 0; i < grid.Length; i++)
        {
            if (grid[i]) pixelsTraced++;
        }

        var result = o.Mode == TraceMode.Centreline
            ? BuildCentrelines(grid, gw, gh, o, warnings)
            : BuildOutlines(grid, gw, gh, o, warnings);

        if (!backToSource.IsIdentity)
        {
            for (var i = 0; i < result.Count; i++) result[i] = result[i].Transformed(backToSource);
        }

        return new TraceResult(result, pixelsTraced, o.Mode, warnings);
    }

    private static string TooMuchDetail(int points, TraceMode mode) =>
        $"Stopped at {points:N0} points. This image has more detail than a laser can " +
        (mode == TraceMode.Centreline
            ? "follow — try raising the threshold, or blurring the source first."
            : "cut — try raising the threshold, simplifying harder, or blurring the source first.");

    /// <summary>
    /// Threshold into a padded grid, so a region touching the image edge still
    /// closes properly instead of running off the array.
    /// </summary>
    private static (bool[] Grid, int Width, int Height) Binarise(RasterImage source, TraceOptions o)
    {
        var w = source.Width;
        var h = source.Height;
        var gw = w + 2;
        var gh = h + 2;
        var grid = new bool[gw * gh];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var dark = source.Pixels[y * w + x] < o.Threshold;
                if (o.Invert) dark = !dark;
                grid[(y + 1) * gw + (x + 1)] = dark;
            }
        }

        if (o.Despeckle) Despeckle(grid, gw, gh);
        return (grid, gw, gh);
    }

    private static List<Polyline> BuildOutlines(bool[] grid, int gw, int gh, TraceOptions o, List<string> warnings)
    {
        var contours = MarchingSquares(grid, gw, gh);
        var result = new List<Polyline>(contours.Count);
        var points = 0;

        foreach (var contour in contours)
        {
            if (o.MaxPoints > 0 && points >= o.MaxPoints)
            {
                warnings.Add(TooMuchDetail(points, TraceMode.Outline));
                break;
            }

            if (contour.Count < 4) continue;

            var polyline = new Polyline(contour, closed: true);
            if (Math.Abs(polyline.SignedArea) < o.MinimumAreaPx) continue;

            var simplified = o.SimplifyTolerancePx > 0
                ? PathOps.Simplify(polyline, o.SimplifyTolerancePx)
                : polyline;

            if (simplified.Count < 3) continue;

            var smoothed = o.SmoothPasses > 0 ? PathOps.Smooth(simplified, o.SmoothPasses) : simplified;
            smoothed.IsClosed = true;

            // Shift back out of the padded grid into image coordinates.
            result.Add(smoothed.Transformed(Matrix2D.Translate(-1, -1)));
            points += smoothed.Count;
        }

        return result;
    }

    private static List<Polyline> BuildCentrelines(bool[] grid, int gw, int gh, TraceOptions o, List<string> warnings)
    {
        var skeleton = Thin(grid, gw, gh);
        var strokes = TraceSkeleton(skeleton, gw, gh);
        var result = new List<Polyline>(strokes.Count);
        var points = 0;

        foreach (var stroke in strokes)
        {
            if (o.MaxPoints > 0 && points >= o.MaxPoints)
            {
                warnings.Add(TooMuchDetail(points, TraceMode.Centreline));
                break;
            }

            if (stroke.Count < 2) continue;

            // A walk that returns to its own start is a loop — the inside of an O.
            var closed = stroke.Count > 3 && (stroke[0] - stroke[^1]).Length < 1.5;
            var polyline = new Polyline(stroke, closed);
            if (polyline.Length < o.MinimumLengthPx) continue;

            var simplified = o.SimplifyTolerancePx > 0
                ? PathOps.Simplify(polyline, o.SimplifyTolerancePx)
                : polyline;

            if (simplified.Count < 2) continue;

            var smoothed = o.SmoothPasses > 0 && simplified.Count > 2
                ? PathOps.Smooth(simplified, o.SmoothPasses)
                : simplified;
            smoothed.IsClosed = closed;

            result.Add(smoothed.Transformed(Matrix2D.Translate(-1, -1)));
            points += smoothed.Count;
        }

        return result;
    }

    /// <summary>
    /// The thinned skeleton of the binarised image, as a raster. Centreline mode
    /// walks this; exposing it lets a preview show the spine the paths will follow.
    /// </summary>
    public static RasterImage Skeleton(RasterImage source, TraceOptions? options = null)
    {
        var o = options ?? TraceOptions.Default;
        var (grid, gw, gh) = Binarise(source, o);
        var thinned = Thin(grid, gw, gh);

        var px = new byte[source.Width * source.Height];
        Array.Fill(px, (byte)255);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                if (thinned[(y + 1) * gw + (x + 1)]) px[y * source.Width + x] = 0;
            }
        }
        return new RasterImage(source.Width, source.Height, px);
    }

    /// <summary>
    /// Otsu's method — the threshold that best separates the histogram into two
    /// classes. A far better starting point than a fixed 128, which blows out
    /// anything photographed under warm light.
    /// </summary>
    public static int AutoThreshold(RasterImage source)
    {
        var histogram = new int[256];
        foreach (var p in source.Pixels) histogram[p]++;

        var total = source.Pixels.Length;
        if (total == 0) return 128;

        double sum = 0;
        for (var i = 0; i < 256; i++) sum += i * (double)histogram[i];

        double sumB = 0;
        var weightB = 0;
        var plateauStart = 0;
        var plateauEnd = 0;
        var bestVariance = -1.0;

        for (var t = 0; t < 256; t++)
        {
            weightB += histogram[t];
            if (weightB == 0) continue;

            var weightF = total - weightB;
            if (weightF == 0) break;

            sumB += t * (double)histogram[t];
            var meanB = sumB / weightB;
            var meanF = (sum - sumB) / weightF;
            var variance = (double)weightB * weightF * (meanB - meanF) * (meanB - meanF);

            // Every empty level between two peaks separates them equally well, so
            // the best variance is usually a plateau rather than a point. Taking
            // the first one is what Otsu is normally written to do, and on clean
            // line art — black on white, nothing in between — that plateau runs
            // the whole way and the answer comes back as 1: correct, but sitting
            // against the end of the slider, one nudge from finding nothing.
            // Take the middle of the plateau instead.
            if (variance > bestVariance + 1e-9)
            {
                bestVariance = variance;
                plateauStart = t;
                plateauEnd = t;
            }
            else if (variance >= bestVariance - 1e-9)
            {
                plateauEnd = t;
            }
        }

        // The scan tests "pixel <= t"; the tracer tests "pixel < threshold".
        return Math.Clamp((plateauStart + plateauEnd) / 2 + 1, 1, 255);
    }

    /// <summary>Remove single set pixels with no set orthogonal neighbour.</summary>
    private static void Despeckle(bool[] grid, int gw, int gh)
    {
        var copy = (bool[])grid.Clone();
        for (var y = 1; y < gh - 1; y++)
        {
            for (var x = 1; x < gw - 1; x++)
            {
                var i = y * gw + x;
                if (!copy[i]) continue;
                var neighbours = (copy[i - 1] ? 1 : 0) + (copy[i + 1] ? 1 : 0) +
                                 (copy[i - gw] ? 1 : 0) + (copy[i + gw] ? 1 : 0);
                if (neighbours == 0) grid[i] = false;
            }
        }
    }

    // Orthogonal neighbours first, so a skeleton walk prefers a straight step to a
    // diagonal one and does not cut corners off its own staircase.
    private static readonly int[] Dx8 = [1, 0, -1, 0, 1, -1, -1, 1];
    private static readonly int[] Dy8 = [0, 1, 0, -1, 1, 1, -1, -1];

    /// <summary>The index in <see cref="Dx8"/> pointing the opposite way.</summary>
    private static readonly int[] Opposite = [2, 3, 0, 1, 6, 7, 4, 5];

    /// <summary>Edge slot for each direction, or -1 for the four that mirror another.</summary>
    private static readonly int[] Canonical = [0, 1, -1, -1, 2, 3, -1, -1];

    /// <summary>
    /// Zhang–Suen thinning. Erodes each dark region to a one-pixel spine while
    /// preserving connectivity, which is the whole point — a thinning that breaks
    /// a stroke in half turns one letter into two paths.
    /// </summary>
    private static bool[] Thin(bool[] input, int gw, int gh)
    {
        var grid = (bool[])input.Clone();
        var doomed = new List<int>();

        bool At(int x, int y) => x >= 0 && y >= 0 && x < gw && y < gh && grid[y * gw + x];

        // Only pixels next to something that just went away can become deletable,
        // so carry a candidate set forward instead of rescanning the whole image
        // every pass. On a photo-sized region that is the difference between a
        // minute and a moment.
        var candidates = new List<int>();
        var next = new List<int>();
        var queued = new bool[grid.Length];

        for (var i = 0; i < grid.Length; i++)
        {
            if (grid[i]) { candidates.Add(i); queued[i] = true; }
        }

        for (var pass = 0; pass < 400 && candidates.Count > 0; pass++)
        {
            var changed = false;

            for (var step = 0; step < 2; step++)
            {
                doomed.Clear();

                foreach (var cell in candidates)
                {
                    {
                        var x = cell % gw;
                        var y = cell / gw;
                        if (x < 1 || y < 1 || x >= gw - 1 || y >= gh - 1) continue;
                        if (!grid[cell]) continue;

                        // P2..P9, clockwise from north.
                        var p2 = At(x, y - 1);
                        var p3 = At(x + 1, y - 1);
                        var p4 = At(x + 1, y);
                        var p5 = At(x + 1, y + 1);
                        var p6 = At(x, y + 1);
                        var p7 = At(x - 1, y + 1);
                        var p8 = At(x - 1, y);
                        var p9 = At(x - 1, y - 1);

                        var b = (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0) +
                                (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
                        if (b < 2 || b > 6) continue;

                        var a = Transitions(p2, p3, p4, p5, p6, p7, p8, p9);
                        if (a != 1) continue;

                        var ok = step == 0
                            ? (!p2 || !p4 || !p6) && (!p4 || !p6 || !p8)
                            : (!p2 || !p4 || !p8) && (!p2 || !p6 || !p8);

                        if (ok) doomed.Add(cell);
                    }
                }

                foreach (var i in doomed) grid[i] = false;
                if (doomed.Count > 0) changed = true;

                // Anything still standing beside a deletion is worth re-testing;
                // so is anything that survived this round.
                next.Clear();
                Array.Clear(queued);

                void Consider(int index)
                {
                    if (index < 0 || index >= grid.Length || !grid[index] || queued[index]) return;
                    queued[index] = true;
                    next.Add(index);
                }

                foreach (var i in doomed)
                {
                    for (var k = 0; k < 8; k++) Consider(i + Dy8[k] * gw + Dx8[k]);
                }
                foreach (var i in candidates) Consider(i);

                (candidates, next) = (next, candidates);
            }

            if (!changed) break;
        }

        RemoveRedundant(grid, gw, gh);
        return grid;
    }

    /// <summary>
    /// Zhang–Suen leaves diagonals two pixels wide. Its two-step masks are what
    /// stop a parallel pass from eating a line in half, and the price is a
    /// staircase of pixel pairs. Every pixel of a two-wide run then reads as a
    /// branch point, and the walk shatters into overlapping fragments — one line
    /// traced three times over.
    ///
    /// So delete every pixel that is not holding anything together: not a loose
    /// end, and with all its neighbours still reachable from one another once it
    /// is gone. The test has to look two pixels out, not one — in a two-wide run
    /// each pixel's neighbours are connected only *via its partner*, which a 3×3
    /// window cannot see, so a 3×3 test keeps both and thins nothing.
    ///
    /// Applied sequentially rather than in parallel, which is what makes it safe:
    /// each deletion is individually connectivity-preserving and the next test
    /// sees the result. It cannot shorten a stroke either, because removing the
    /// pixel next to a loose end would strand that end in a component of its own.
    /// </summary>
    private static void RemoveRedundant(bool[] grid, int gw, int gh)
    {
        const int Radius = 2;
        const int Span = Radius * 2 + 1;

        var window = new bool[Span * Span];
        var reached = new bool[Span * Span];
        var neighbours = new List<int>(8);
        var stack = new Stack<int>(Span * Span);

        bool At(int x, int y) => x >= 0 && y >= 0 && x < gw && y < gh && grid[y * gw + x];

        bool Redundant(int px, int py)
        {
            neighbours.Clear();
            Array.Clear(window);
            Array.Clear(reached);

            for (var j = 0; j < Span; j++)
            {
                for (var i = 0; i < Span; i++)
                {
                    // The pixel under test is taken as already gone.
                    if (i == Radius && j == Radius) continue;
                    if (!At(px - Radius + i, py - Radius + j)) continue;

                    window[j * Span + i] = true;
                    if (Math.Abs(i - Radius) <= 1 && Math.Abs(j - Radius) <= 1) neighbours.Add(j * Span + i);
                }
            }

            // One neighbour or none is a loose end. Keep it, or strokes lose their tips.
            if (neighbours.Count < 2) return false;

            stack.Clear();
            stack.Push(neighbours[0]);
            reached[neighbours[0]] = true;
            var found = 1;

            while (stack.Count > 0 && found < neighbours.Count)
            {
                var cell = stack.Pop();
                var cx = cell % Span;
                var cy = cell / Span;

                for (var k = 0; k < 8; k++)
                {
                    var nx = cx + Dx8[k];
                    var ny = cy + Dy8[k];
                    if (nx < 0 || ny < 0 || nx >= Span || ny >= Span) continue;

                    var index = ny * Span + nx;
                    if (!window[index] || reached[index]) continue;

                    reached[index] = true;
                    stack.Push(index);
                    if (neighbours.Contains(index)) found++;
                }
            }

            return found == neighbours.Count;
        }

        // Same candidate-set trick as the thinning above.
        var candidates = new List<int>();
        var next = new List<int>();
        var queued = new bool[grid.Length];

        for (var y = 1; y < gh - 1; y++)
        {
            for (var x = 1; x < gw - 1; x++)
            {
                var i = y * gw + x;
                if (grid[i]) { candidates.Add(i); queued[i] = true; }
            }
        }

        for (var pass = 0; pass < 16 && candidates.Count > 0; pass++)
        {
            next.Clear();
            Array.Clear(queued);
            var changed = false;

            void Consider(int index)
            {
                if (index < 0 || index >= grid.Length || !grid[index] || queued[index]) return;
                var cx = index % gw;
                var cy = index / gw;
                if (cx < 1 || cy < 1 || cx >= gw - 1 || cy >= gh - 1) return;
                queued[index] = true;
                next.Add(index);
            }

            foreach (var cell in candidates)
            {
                if (!grid[cell]) continue;
                var x = cell % gw;
                var y = cell / gw;
                if (x < 1 || y < 1 || x >= gw - 1 || y >= gh - 1) continue;
                if (!Redundant(x, y)) continue;

                grid[cell] = false;
                changed = true;

                // The pixels around a deletion are the only ones whose answer can
                // have changed, plus a ring further out because the test looks
                // two pixels away.
                for (var j = -Radius; j <= Radius; j++)
                {
                    for (var i = -Radius; i <= Radius; i++) Consider(cell + j * gw + i);
                }
            }

            if (!changed) break;
            (candidates, next) = (next, candidates);
        }
    }

    /// <summary>Count 0→1 transitions around the eight-neighbourhood.</summary>
    private static int Transitions(bool p2, bool p3, bool p4, bool p5, bool p6, bool p7, bool p8, bool p9)
    {
        Span<bool> ring = [p2, p3, p4, p5, p6, p7, p8, p9, p2];
        var count = 0;
        for (var i = 0; i < 8; i++)
        {
            if (!ring[i] && ring[i + 1]) count++;
        }
        return count;
    }

    /// <summary>
    /// Walk a thinned image into polylines. Starts at endpoints, then junctions,
    /// then whatever is left (closed loops), consuming each pixel-to-pixel edge
    /// exactly once so a junction produces several strokes rather than one path
    /// that doubles back through it.
    /// </summary>
    private static List<List<Vec2>> TraceSkeleton(bool[] grid, int gw, int gh)
    {
        // Each pixel-to-pixel edge must be walked once. Addressing it directly —
        // four slots per pixel, one per direction that is not the mirror of
        // another — rather than hashing the pair of endpoints. A hash set looks
        // fine here and is a trap: adjacent pixel indices differ by 1 or by a row,
        // so XOR-folding the pair collapses every key into a few thousand hash
        // codes, every add collides, and the walk goes quadratic. On a photo that
        // was ninety seconds of the ninety-one this took.
        var edgeUsed = new bool[grid.Length * 4];
        var paths = new List<List<Vec2>>();

        bool At(int x, int y) => x >= 0 && y >= 0 && x < gw && y < gh && grid[y * gw + x];

        int EdgeSlot(int x, int y, int k)
        {
            // Fold the four mirrored directions onto their partners so both ends
            // of an edge name the same slot.
            if (Canonical[k] < 0)
            {
                x += Dx8[k];
                y += Dy8[k];
                k = Opposite[k];
            }
            return (y * gw + x) * 4 + Canonical[k];
        }

        bool ClaimEdge(int x, int y, int k)
        {
            var slot = EdgeSlot(x, y, k);
            if (edgeUsed[slot]) return false;
            edgeUsed[slot] = true;
            return true;
        }

        int Degree(int x, int y)
        {
            var n = 0;
            for (var k = 0; k < 8; k++)
            {
                if (At(x + Dx8[k], y + Dy8[k])) n++;
            }
            return n;
        }

        // Raw neighbour count is the wrong question. Thinning leaves diagonals two
        // pixels wide, and every pixel of a two-wide run has three or four
        // neighbours while still being an ordinary point along a curve. The
        // crossing number — how many separate groups of set neighbours surround
        // the pixel — is what actually distinguishes them: one group is a loose
        // end, two is somewhere along a line, three or more is a real branch.
        int Arms(int x, int y) => Transitions(
            At(x, y - 1), At(x + 1, y - 1), At(x + 1, y), At(x + 1, y + 1),
            At(x, y + 1), At(x - 1, y + 1), At(x - 1, y), At(x - 1, y - 1));

        bool IsEndpoint(int x, int y) => Degree(x, y) <= 1;
        bool IsJunction(int x, int y) => Arms(x, y) >= 3;

        bool HasFreeEdge(int x, int y)
        {
            for (var k = 0; k < 8; k++)
            {
                if (At(x + Dx8[k], y + Dy8[k]) && !edgeUsed[EdgeSlot(x, y, k)]) return true;
            }
            return false;
        }

        List<Vec2>? Walk(int sx, int sy)
        {
            var path = new List<Vec2> { new(sx + 0.5, sy + 0.5) };
            var cx = sx;
            var cy = sy;
            var guard = 0;
            var limit = gw * gh + 8;

            while (guard++ < limit)
            {
                var stepped = false;
                for (var k = 0; k < 8; k++)
                {
                    var nx = cx + Dx8[k];
                    var ny = cy + Dy8[k];
                    if (!At(nx, ny)) continue;
                    if (!ClaimEdge(cx, cy, k)) continue;

                    cx = nx;
                    cy = ny;
                    path.Add(new Vec2(cx + 0.5, cy + 0.5));
                    stepped = true;
                    break;
                }

                if (!stepped) break;
                if (cx == sx && cy == sy) break;
                // Stop at loose ends and branch points; the next walk picks up there.
                if (IsEndpoint(cx, cy) || IsJunction(cx, cy)) break;
            }

            return path.Count >= 2 ? path : null;
        }

        void Sweep(Func<int, int, bool> matches)
        {
            for (var y = 1; y < gh - 1; y++)
            {
                for (var x = 1; x < gw - 1; x++)
                {
                    if (!grid[y * gw + x]) continue;
                    if (!matches(x, y)) continue;

                    while (HasFreeEdge(x, y))
                    {
                        if (Walk(x, y) is { } path) paths.Add(path);
                        else break;
                    }
                }
            }
        }

        Sweep(IsEndpoint);                    // loose ends first, so strokes run end to end
        Sweep(IsJunction);                    // then out of every branch point
        Sweep((_, _) => true);                // then closed loops nothing else touched

        return paths;
    }

    /// <summary>
    /// Marching squares over the 2×2 neighbourhoods of the grid, walking each
    /// boundary until it returns to its start.
    /// </summary>
    private static List<List<Vec2>> MarchingSquares(bool[] grid, int gw, int gh)
    {
        var contours = new List<List<Vec2>>();
        // One visited flag per cell per direction of entry keeps a figure-of-eight
        // boundary from being walked twice.
        var visited = new bool[(gw - 1) * (gh - 1)];

        bool At(int x, int y) => x >= 0 && y >= 0 && x < gw && y < gh && grid[y * gw + x];

        int CellCase(int x, int y) =>
            (At(x, y) ? 1 : 0) |
            (At(x + 1, y) ? 2 : 0) |
            (At(x + 1, y + 1) ? 4 : 0) |
            (At(x, y + 1) ? 8 : 0);

        for (var sy = 0; sy < gh - 1; sy++)
        {
            for (var sx = 0; sx < gw - 1; sx++)
            {
                var startCase = CellCase(sx, sy);
                if (startCase is 0 or 15) continue;
                if (visited[sy * (gw - 1) + sx]) continue;

                var contour = new List<Vec2>();
                var x = sx;
                var y = sy;
                var previousDirection = -1;
                var guard = 0;
                var maxSteps = (gw + gh) * 8 + 64;

                while (guard++ < maxSteps)
                {
                    var index = y * (gw - 1) + x;
                    if (x < 0 || y < 0 || x >= gw - 1 || y >= gh - 1) break;

                    var c = CellCase(x, y);
                    if (c is 0 or 15) break;

                    visited[index] = true;
                    contour.Add(new Vec2(x + 0.5, y + 0.5));

                    // 0 = right, 1 = down, 2 = left, 3 = up
                    var direction = c switch
                    {
                        1 => 3,
                        2 => 0,
                        3 => 0,
                        4 => 1,
                        // Saddle: resolve using where we came from, or the boundary tears.
                        5 => previousDirection == 0 ? 3 : 1,
                        6 => 1,
                        7 => 1,
                        8 => 2,
                        9 => 3,
                        10 => previousDirection == 1 ? 0 : 2,
                        11 => 0,
                        12 => 2,
                        13 => 3,
                        14 => 2,
                        _ => -1,
                    };

                    if (direction < 0) break;
                    previousDirection = direction;

                    switch (direction)
                    {
                        case 0: x++; break;
                        case 1: y++; break;
                        case 2: x--; break;
                        default: y--; break;
                    }

                    if (x == sx && y == sy) break;
                }

                if (contour.Count >= 4) contours.Add(contour);
            }
        }

        return contours;
    }

    /// <summary>
    /// Place an already-computed trace on the bed at a real-world size.
    /// The Y flip happens here: image row 0 is the top, machine Y grows upward.
    /// </summary>
    public static PathShape ToShape(TraceResult result, int sourceWidth, int sourceHeight,
                                    double widthMm, double heightMm, string? name = null)
    {
        var scaleX = widthMm / Math.Max(1, sourceWidth);
        var scaleY = heightMm / Math.Max(1, sourceHeight);
        var transform = Matrix2D.Translate(0, heightMm) * Matrix2D.Scale(scaleX, -scaleY);

        return new PathShape(result.Contours.Select(c => c.Transformed(transform)))
        {
            Name = name ?? "Traced image",
        };
    }

    /// <summary>Trace and place the result on the bed at a real-world size.</summary>
    public static PathShape TraceToShape(RasterImage source, double widthMm, double heightMm, TraceOptions? options = null)
    {
        var result = Trace(source, options);
        return ToShape(result, source.Width, source.Height, widthMm, heightMm);
    }
}
