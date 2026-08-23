using OpenBurn.Camera;

namespace OpenBurn.Vision;

/// <summary>
/// Radial lens distortion.
///
/// Wide-angle cameras — which is every camera anyone mounts inside a laser
/// enclosure, because they need to see the whole bed from thirty centimetres —
/// bow straight lines outward. Correcting that has to happen before the
/// perspective solve, or the four corner points sit on a curve and the homography
/// fits a shape the bed does not have.
/// </summary>
public readonly record struct LensParameters(double K1, double K2 = 0, double CentreX = 0.5, double CentreY = 0.5)
{
    public static readonly LensParameters None = new(0);
    public bool IsIdentity => Math.Abs(K1) < 1e-9 && Math.Abs(K2) < 1e-9;
}

public static class LensModel
{
    /// <summary>
    /// Remove radial distortion by inverse-mapping every destination pixel back
    /// into the distorted source and sampling bilinearly. Working backwards is what
    /// avoids the holes a forward map leaves.
    /// </summary>
    public static CameraFrame Undistort(CameraFrame source, LensParameters parameters)
    {
        if (parameters.IsIdentity) return source.Clone();

        var width = source.Width;
        var height = source.Height;
        var result = CameraFrame.Create(width, height);

        var cx = parameters.CentreX * width;
        var cy = parameters.CentreY * height;
        // Normalise by the half-diagonal so k1 means the same thing at any resolution.
        var norm = Math.Sqrt(cx * cx + cy * cy);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dx = (x + 0.5 - cx) / norm;
                var dy = (y + 0.5 - cy) / norm;
                var r2 = dx * dx + dy * dy;
                var scale = 1 + parameters.K1 * r2 + parameters.K2 * r2 * r2;

                var sx = cx + dx * scale * norm;
                var sy = cy + dy * scale * norm;

                Sample(source, sx - 0.5, sy - 0.5, result, x, y);
            }
        }

        return result;
    }

    /// <summary>Apply barrel distortion. Used by the synthetic camera to produce a realistic test image.</summary>
    public static CameraFrame ApplyBarrel(CameraFrame source, double amount)
    {
        if (Math.Abs(amount) < 1e-9) return source.Clone();
        // Applying +k is the same operation as undistorting with −k.
        return Undistort(source, new LensParameters(-amount));
    }

    internal static void Sample(CameraFrame source, double sx, double sy, CameraFrame destination, int dx, int dy)
    {
        if (sx < 0 || sy < 0 || sx >= source.Width - 1 || sy >= source.Height - 1)
        {
            destination.Set(dx, dy, 0, 0, 0, 0);
            return;
        }

        var x0 = (int)sx;
        var y0 = (int)sy;
        var fx = sx - x0;
        var fy = sy - y0;

        var i00 = (y0 * source.Width + x0) * 4;
        var i10 = i00 + 4;
        var i01 = i00 + source.Width * 4;
        var i11 = i01 + 4;

        var p = source.Pixels;
        for (var c = 0; c < 4; c++)
        {
            var value = p[i00 + c] * (1 - fx) * (1 - fy) +
                        p[i10 + c] * fx * (1 - fy) +
                        p[i01 + c] * (1 - fx) * fy +
                        p[i11 + c] * fx * fy;
            destination.Pixels[(dy * destination.Width + dx) * 4 + c] = (byte)Math.Clamp(Math.Round(value), 0, 255);
        }
    }

    /// <summary>
    /// Estimate k1 from a line that should be straight but is not.
    ///
    /// Sweeps candidate values and keeps the one that minimises the points'
    /// deviation from their own best-fit line. Crude compared with a full
    /// checkerboard calibration, but it needs one photograph of one straight edge,
    /// which is a calibration a real person will actually perform.
    /// </summary>
    public static double EstimateK1(IReadOnlyList<(double X, double Y)> pointsOnStraightLine, int width, int height)
    {
        if (pointsOnStraightLine.Count < 3) return 0;

        var cx = width / 2.0;
        var cy = height / 2.0;
        var norm = Math.Sqrt(cx * cx + cy * cy);

        var best = 0.0;
        var bestError = double.MaxValue;

        for (var k = -0.6; k <= 0.6001; k += 0.005)
        {
            var corrected = new List<(double X, double Y)>(pointsOnStraightLine.Count);
            foreach (var (x, y) in pointsOnStraightLine)
            {
                var dx = (x - cx) / norm;
                var dy = (y - cy) / norm;
                var r2 = dx * dx + dy * dy;
                var scale = 1 + k * r2;
                corrected.Add((cx + dx * scale * norm, cy + dy * scale * norm));
            }

            var error = StraightnessError(corrected);
            if (error < bestError)
            {
                bestError = error;
                best = k;
            }
        }

        return best;
    }

    /// <summary>Sum of squared perpendicular distances from the best-fit line.</summary>
    private static double StraightnessError(IReadOnlyList<(double X, double Y)> points)
    {
        var n = points.Count;
        var meanX = points.Average(p => p.X);
        var meanY = points.Average(p => p.Y);

        double sxx = 0, syy = 0, sxy = 0;
        foreach (var (x, y) in points)
        {
            var dx = x - meanX;
            var dy = y - meanY;
            sxx += dx * dx;
            syy += dy * dy;
            sxy += dx * dy;
        }

        // Smaller eigenvalue of the covariance matrix is the residual across the line.
        var trace = sxx + syy;
        var det = sxx * syy - sxy * sxy;
        var discriminant = Math.Sqrt(Math.Max(0, trace * trace / 4 - det));
        return Math.Max(0, trace / 2 - discriminant) / n;
    }
}
