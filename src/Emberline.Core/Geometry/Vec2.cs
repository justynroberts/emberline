namespace Emberline.Core.Geometry;

/// <summary>
/// A point or vector in millimetres. Everything inside Emberline is millimetres;
/// inches exist only at the UI boundary and in importers.
/// </summary>
public readonly record struct Vec2(double X, double Y)
{
    public static readonly Vec2 Zero = new(0, 0);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSquared => X * X + Y * Y;

    public Vec2 Normalized
    {
        get
        {
            var l = Length;
            return l > 1e-12 ? new Vec2(X / l, Y / l) : Zero;
        }
    }

    public double Dot(Vec2 other) => X * other.X + Y * other.Y;

    /// <summary>Z component of the 3D cross product — sign tells you which side a point is on.</summary>
    public double Cross(Vec2 other) => X * other.Y - Y * other.X;

    public double DistanceTo(Vec2 other) => (other - this).Length;
    public double DistanceSquaredTo(Vec2 other) => (other - this).LengthSquared;

    public Vec2 Rotated(double radians)
    {
        var (s, c) = Math.SinCos(radians);
        return new Vec2(X * c - Y * s, X * s + Y * c);
    }

    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}
