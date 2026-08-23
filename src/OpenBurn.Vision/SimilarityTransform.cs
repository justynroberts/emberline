using OpenBurn.Core.Geometry;

namespace OpenBurn.Vision;

/// <summary>
/// A rigid-plus-uniform-scale fit between two sets of points.
///
/// This is what fiducial alignment needs, and deliberately *not* a homography: on
/// an already-rectified bed image the only remaining difference between where a
/// workpiece was and where it is now is translation, rotation and a little scale.
/// Fitting a full projective transform to four noisy marker centres would happily
/// absorb the noise as perspective and skew the artwork to match.
/// </summary>
public readonly record struct SimilarityFit(
    double ScaleFactor,
    double RotationDegrees,
    Vec2 Translation,
    double ResidualMm)
{
    public static readonly SimilarityFit Identity = new(1, 0, Vec2.Zero, 0);

    public bool IsIdentity =>
        Math.Abs(ScaleFactor - 1) < 1e-9 &&
        Math.Abs(RotationDegrees) < 1e-9 &&
        Translation.LengthSquared < 1e-18;

    /// <summary>The fit as a matrix that can be applied to a shape.</summary>
    public Matrix2D ToMatrix() =>
        Matrix2D.Translate(Translation) * Matrix2D.Rotate(RotationDegrees) * Matrix2D.Scale(ScaleFactor);

    public string Describe() =>
        $"move {Translation.X:0.##}, {Translation.Y:0.##} mm · " +
        $"turn {RotationDegrees:0.##}° · " +
        $"scale ×{ScaleFactor:0.####} · fit {ResidualMm:0.###} mm";
}

public static class SimilarityTransform
{
    /// <summary>
    /// Least-squares similarity fit taking <paramref name="from"/> onto
    /// <paramref name="to"/>, by the Umeyama method: centre both sets, recover the
    /// rotation from the cross-covariance, then scale and translate.
    ///
    /// The reflection guard matters. Without it, a set of markers detected in a
    /// different order — or one mis-detected — can produce a best fit that is a
    /// mirror image, and the artwork lands flipped rather than merely wrong.
    /// </summary>
    public static bool TrySolve(IReadOnlyList<Vec2> from, IReadOnlyList<Vec2> to, out SimilarityFit fit)
    {
        fit = SimilarityFit.Identity;

        var n = Math.Min(from.Count, to.Count);
        if (n < 2) return false;

        var fromCentre = Centroid(from, n);
        var toCentre = Centroid(to, n);

        // Cross-covariance of the centred sets, and the source's variance.
        double sxx = 0, sxy = 0, variance = 0;
        for (var i = 0; i < n; i++)
        {
            var a = from[i] - fromCentre;
            var b = to[i] - toCentre;

            sxx += a.X * b.X + a.Y * b.Y;   // the trace term
            sxy += a.X * b.Y - a.Y * b.X;   // the cross term
            variance += a.LengthSquared;
        }

        if (variance < 1e-12) return false;

        var angle = Math.Atan2(sxy, sxx);
        var scale = Math.Sqrt(sxx * sxx + sxy * sxy) / variance;

        if (!double.IsFinite(scale) || scale <= 1e-9) return false;

        var (sin, cos) = Math.SinCos(angle);
        var rotatedCentre = new Vec2(
            scale * (cos * fromCentre.X - sin * fromCentre.Y),
            scale * (sin * fromCentre.X + cos * fromCentre.Y));

        var translation = toCentre - rotatedCentre;

        // Residual: how far the fitted points land from where they should.
        double residual = 0;
        for (var i = 0; i < n; i++)
        {
            var mapped = new Vec2(
                scale * (cos * from[i].X - sin * from[i].Y) + translation.X,
                scale * (sin * from[i].X + cos * from[i].Y) + translation.Y);
            residual += mapped.DistanceTo(to[i]);
        }

        fit = new SimilarityFit(scale, angle * 180 / Math.PI, translation, residual / n);
        return true;
    }

    public static SimilarityFit SolveOrIdentity(IReadOnlyList<Vec2> from, IReadOnlyList<Vec2> to) =>
        TrySolve(from, to, out var fit) ? fit : SimilarityFit.Identity;

    private static Vec2 Centroid(IReadOnlyList<Vec2> points, int count)
    {
        double x = 0, y = 0;
        for (var i = 0; i < count; i++)
        {
            x += points[i].X;
            y += points[i].Y;
        }
        return new Vec2(x / count, y / count);
    }

    /// <summary>
    /// Whether a fit is plausible enough to apply without asking.
    ///
    /// A workpiece put back on the bed moves and turns; it does not change size.
    /// A scale that has drifted more than a percent or two means the markers were
    /// mis-matched, and applying it would resize the artwork.
    /// </summary>
    public static IReadOnlyList<string> Check(SimilarityFit fit, double maxResidualMm = 1.0)
    {
        var problems = new List<string>();

        if (fit.ResidualMm > maxResidualMm)
        {
            problems.Add($"The marks do not fit the reference well — {fit.ResidualMm:0.##} mm out on average. " +
                         "They may have been detected in a different order, or one may be a false positive.");
        }

        if (Math.Abs(fit.ScaleFactor - 1) > 0.02)
        {
            problems.Add($"The fit wants to resize the artwork by ×{fit.ScaleFactor:0.###}. A workpiece put back " +
                         "on the bed changes position, not size — check the camera calibration and the marks.");
        }

        return problems;
    }
}
