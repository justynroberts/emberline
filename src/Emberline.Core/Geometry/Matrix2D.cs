namespace Emberline.Core.Geometry;

/// <summary>
/// 2D affine transform stored in SVG's matrix(a,b,c,d,e,f) ordering, so an SVG
/// transform attribute maps in without re-deriving anything:
///   x' = a·x + c·y + e
///   y' = b·x + d·y + f
/// </summary>
public readonly record struct Matrix2D(double A, double B, double C, double D, double E, double F)
{
    public static readonly Matrix2D Identity = new(1, 0, 0, 1, 0, 0);

    public static Matrix2D Translate(double tx, double ty) => new(1, 0, 0, 1, tx, ty);
    public static Matrix2D Translate(Vec2 t) => Translate(t.X, t.Y);
    public static Matrix2D Scale(double s) => new(s, 0, 0, s, 0, 0);
    public static Matrix2D Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);
    public static Matrix2D Skew(double axDeg, double ayDeg) =>
        new(1, Math.Tan(ayDeg * Math.PI / 180), Math.Tan(axDeg * Math.PI / 180), 1, 0, 0);

    public static Matrix2D Rotate(double degrees)
    {
        var (s, c) = Math.SinCos(degrees * Math.PI / 180);
        return new Matrix2D(c, s, -s, c, 0, 0);
    }

    /// <summary>
    /// The rotation this transform applies, in degrees.
    ///
    /// Read from where the x axis ends up, which is correct for any combination of
    /// rotation, translation and uniform scale — the cases a design actually
    /// contains. A non-uniform scale makes "the rotation" ambiguous, and this
    /// reports the angle of the x axis rather than pretending otherwise.
    /// </summary>
    public double RotationDegrees => Math.Atan2(B, A) * 180 / Math.PI;

    public static Matrix2D RotateAbout(double degrees, Vec2 pivot) =>
        Translate(pivot) * Rotate(degrees) * Translate(-pivot);

    /// <summary>Applies <paramref name="right"/> first, then <paramref name="left"/>.</summary>
    public static Matrix2D operator *(Matrix2D left, Matrix2D right) => new(
        left.A * right.A + left.C * right.B,
        left.B * right.A + left.D * right.B,
        left.A * right.C + left.C * right.D,
        left.B * right.C + left.D * right.D,
        left.A * right.E + left.C * right.F + left.E,
        left.B * right.E + left.D * right.F + left.F);

    public Vec2 Apply(Vec2 p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);
    public Vec2 Apply(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);

    /// <summary>Transforms a direction — translation is deliberately ignored.</summary>
    public Vec2 ApplyVector(Vec2 v) => new(A * v.X + C * v.Y, B * v.X + D * v.Y);

    public double Determinant => A * D - B * C;

    /// <summary>Average linear scale. Keeps curve-flattening tolerance honest under transform.</summary>
    public double ScaleFactor
    {
        get
        {
            var det = Math.Abs(Determinant);
            return det > 1e-12 ? Math.Sqrt(det) : 1;
        }
    }

    public bool TryInvert(out Matrix2D inverse)
    {
        var det = Determinant;
        if (Math.Abs(det) < 1e-12)
        {
            inverse = Identity;
            return false;
        }
        var ia = D / det;
        var ib = -B / det;
        var ic = -C / det;
        var id = A / det;
        inverse = new Matrix2D(ia, ib, ic, id, -(ia * E + ic * F), -(ib * E + id * F));
        return true;
    }

    public bool IsIdentity =>
        Math.Abs(A - 1) < 1e-12 && Math.Abs(B) < 1e-12 && Math.Abs(C) < 1e-12 &&
        Math.Abs(D - 1) < 1e-12 && Math.Abs(E) < 1e-12 && Math.Abs(F) < 1e-12;
}
