using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;

namespace OpenBurn.Cam.Trace;

public sealed record TraceOptions
{
    /// <summary>Pixels darker than this are treated as ink. 0–255.</summary>
    public int Threshold { get; init; } = 128;

    /// <summary>Douglas–Peucker tolerance in pixels. Higher gives fewer, straighter segments.</summary>
    public double SimplifyTolerancePx { get; init; } = 0.8;

    /// <summary>Chaikin smoothing passes. Two takes the stair-steps off without losing corners.</summary>
    public int SmoothPasses { get; init; } = 2;

    /// <summary>Discard contours enclosing fewer pixels than this — speckle, not artwork.</summary>
    public int MinimumAreaPx { get; init; } = 12;

    /// <summary>Remove isolated pixels before tracing.</summary>
    public bool Despeckle { get; init; } = true;

    /// <summary>Trace light regions instead of dark ones.</summary>
    public bool Invert { get; init; }

    public static readonly TraceOptions Default = new();
}

public sealed record TraceResult(IReadOnlyList<Polyline> Contours, int PixelsTraced)
{
    public int ContourCount => Contours.Count;
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
        var w = source.Width;
        var h = source.Height;

        // Binarise into a padded grid so contours touching the image edge still
        // close properly instead of running off the array.
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

        var pixelsTraced = 0;
        for (var i = 0; i < grid.Length; i++)
        {
            if (grid[i]) pixelsTraced++;
        }

        var contours = MarchingSquares(grid, gw, gh);

        var result = new List<Polyline>(contours.Count);
        foreach (var contour in contours)
        {
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
        }

        return new TraceResult(result, pixelsTraced);
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
    /// Trace and place the result on the bed at a real-world size.
    /// The Y flip happens here: image row 0 is the top, machine Y grows upward.
    /// </summary>
    public static PathShape TraceToShape(RasterImage source, double widthMm, double heightMm, TraceOptions? options = null)
    {
        var result = Trace(source, options);
        var scaleX = widthMm / source.Width;
        var scaleY = heightMm / source.Height;
        var transform = Matrix2D.Translate(0, heightMm) * Matrix2D.Scale(scaleX, -scaleY);

        var shape = new PathShape(result.Contours.Select(c => c.Transformed(transform)))
        {
            Name = "Traced image",
        };
        return shape;
    }
}
