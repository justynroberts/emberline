namespace OpenBurn.Vision;

/// <summary>
/// A 3×3 projective transform.
///
/// This is what turns a camera looking at the bed from an angle into a top-down
/// view: four known correspondences are enough to solve it exactly, and once
/// solved every pixel maps to a bed coordinate in millimetres.
/// </summary>
public readonly record struct Homography(
    double M00, double M01, double M02,
    double M10, double M11, double M12,
    double M20, double M21, double M22)
{
    public static readonly Homography Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public (double X, double Y) Apply(double x, double y)
    {
        var w = M20 * x + M21 * y + M22;
        if (Math.Abs(w) < 1e-12) return (0, 0);
        return ((M00 * x + M01 * y + M02) / w, (M10 * x + M11 * y + M12) / w);
    }

    public double Determinant =>
        M00 * (M11 * M22 - M12 * M21) -
        M01 * (M10 * M22 - M12 * M20) +
        M02 * (M10 * M21 - M11 * M20);

    public bool TryInvert(out Homography inverse)
    {
        var det = Determinant;
        if (Math.Abs(det) < 1e-14)
        {
            inverse = Identity;
            return false;
        }

        var i = new Homography(
            (M11 * M22 - M12 * M21) / det,
            (M02 * M21 - M01 * M22) / det,
            (M01 * M12 - M02 * M11) / det,
            (M12 * M20 - M10 * M22) / det,
            (M00 * M22 - M02 * M20) / det,
            (M02 * M10 - M00 * M12) / det,
            (M10 * M21 - M11 * M20) / det,
            (M01 * M20 - M00 * M21) / det,
            (M00 * M11 - M01 * M10) / det);

        inverse = i.Normalised();
        return true;
    }

    /// <summary>Scale so M22 is one, which keeps comparisons and serialisation stable.</summary>
    public Homography Normalised()
    {
        if (Math.Abs(M22) < 1e-14) return this;
        var s = 1.0 / M22;
        return new Homography(M00 * s, M01 * s, M02 * s, M10 * s, M11 * s, M12 * s, M20 * s, M21 * s, 1);
    }

    public static Homography operator *(Homography a, Homography b) => new(
        a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
        a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
        a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
        a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
        a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
        a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
        a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
        a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
        a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);
}

/// <summary>Solves the four-point correspondence that produces a homography.</summary>
public static class HomographySolver
{
    /// <summary>
    /// Solve H mapping <paramref name="from"/> onto <paramref name="to"/>.
    ///
    /// Eight unknowns, eight equations — two per point pair — solved by Gaussian
    /// elimination with partial pivoting. Direct Linear Transform, without the
    /// normalisation step: with exactly four well-spread points from a calibration
    /// target the conditioning is fine, and skipping it keeps the maths inspectable.
    /// </summary>
    public static bool TrySolve(
        IReadOnlyList<(double X, double Y)> from,
        IReadOnlyList<(double X, double Y)> to,
        out Homography homography)
    {
        homography = Homography.Identity;
        if (from.Count < 4 || to.Count < 4) return false;

        var a = new double[8, 9];

        for (var i = 0; i < 4; i++)
        {
            var (x, y) = from[i];
            var (u, v) = to[i];

            var r0 = i * 2;
            a[r0, 0] = x; a[r0, 1] = y; a[r0, 2] = 1;
            a[r0, 3] = 0; a[r0, 4] = 0; a[r0, 5] = 0;
            a[r0, 6] = -u * x; a[r0, 7] = -u * y; a[r0, 8] = u;

            var r1 = r0 + 1;
            a[r1, 0] = 0; a[r1, 1] = 0; a[r1, 2] = 0;
            a[r1, 3] = x; a[r1, 4] = y; a[r1, 5] = 1;
            a[r1, 6] = -v * x; a[r1, 7] = -v * y; a[r1, 8] = v;
        }

        if (!TrySolveLinearSystem(a, 8, out var h)) return false;

        homography = new Homography(h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], 1);
        return true;
    }

    public static Homography SolveOrIdentity(
        IReadOnlyList<(double X, double Y)> from,
        IReadOnlyList<(double X, double Y)> to) =>
        TrySolve(from, to, out var h) ? h : Homography.Identity;

    /// <summary>Gaussian elimination with partial pivoting on an augmented n×(n+1) matrix.</summary>
    internal static bool TrySolveLinearSystem(double[,] matrix, int n, out double[] solution)
    {
        solution = new double[n];

        for (var col = 0; col < n; col++)
        {
            // Pivot on the largest remaining magnitude, or the solve becomes
            // numerically worthless on near-degenerate point sets.
            var pivot = col;
            for (var row = col + 1; row < n; row++)
            {
                if (Math.Abs(matrix[row, col]) > Math.Abs(matrix[pivot, col])) pivot = row;
            }

            if (Math.Abs(matrix[pivot, col]) < 1e-12) return false;

            if (pivot != col)
            {
                for (var k = col; k <= n; k++) (matrix[col, k], matrix[pivot, k]) = (matrix[pivot, k], matrix[col, k]);
            }

            var diagonal = matrix[col, col];
            for (var k = col; k <= n; k++) matrix[col, k] /= diagonal;

            for (var row = 0; row < n; row++)
            {
                if (row == col) continue;
                var factor = matrix[row, col];
                if (Math.Abs(factor) < 1e-18) continue;
                for (var k = col; k <= n; k++) matrix[row, k] -= factor * matrix[col, k];
            }
        }

        for (var i = 0; i < n; i++) solution[i] = matrix[i, n];
        return true;
    }
}
