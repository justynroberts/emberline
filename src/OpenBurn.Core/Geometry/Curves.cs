namespace OpenBurn.Core.Geometry;

/// <summary>
/// Curve flattening. Adaptive subdivision rather than a fixed step count: a
/// 200 mm sweeping arc and a 0.4 mm fillet need very different segment counts,
/// and using a fixed count either wastes G-code lines or produces visible facets.
/// </summary>
public static class Curves
{
    /// <summary>Default chord tolerance in millimetres. Below a laser kerf, so invisible.</summary>
    public const double DefaultTolerance = 0.02;

    private const int MaxDepth = 18;

    public static void FlattenCubic(Polyline into, Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, double tolerance = DefaultTolerance)
    {
        into.Add(p0);
        SubdivideCubic(into, p0, p1, p2, p3, tolerance * tolerance, 0);
        into.Add(p3);
    }

    private static void SubdivideCubic(Polyline into, Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, double tol2, int depth)
    {
        if (depth >= MaxDepth) return;

        // Flatness measured as the control points' distance from the chord.
        var d1 = DistanceToLineSquared(p1, p0, p3);
        var d2 = DistanceToLineSquared(p2, p0, p3);
        if (Math.Max(d1, d2) <= tol2) return;

        var p01 = Vec2.Lerp(p0, p1, 0.5);
        var p12 = Vec2.Lerp(p1, p2, 0.5);
        var p23 = Vec2.Lerp(p2, p3, 0.5);
        var p012 = Vec2.Lerp(p01, p12, 0.5);
        var p123 = Vec2.Lerp(p12, p23, 0.5);
        var mid = Vec2.Lerp(p012, p123, 0.5);

        SubdivideCubic(into, p0, p01, p012, mid, tol2, depth + 1);
        into.Add(mid);
        SubdivideCubic(into, mid, p123, p23, p3, tol2, depth + 1);
    }

    public static void FlattenQuadratic(Polyline into, Vec2 p0, Vec2 p1, Vec2 p2, double tolerance = DefaultTolerance)
    {
        // Degree-elevate to a cubic; one code path is easier to trust than two.
        var c1 = p0 + (p1 - p0) * (2.0 / 3.0);
        var c2 = p2 + (p1 - p2) * (2.0 / 3.0);
        FlattenCubic(into, p0, c1, c2, p2, tolerance);
    }

    /// <summary>
    /// Flatten a circular arc given centre, radius and sweep. Segment count comes
    /// from the sagitta formula so the chord error never exceeds the tolerance.
    /// </summary>
    public static void FlattenArc(Polyline into, Vec2 center, double radius, double startAngle, double sweepAngle,
                                  double tolerance = DefaultTolerance)
    {
        if (radius <= 1e-9 || Math.Abs(sweepAngle) < 1e-12)
        {
            into.Add(center + new Vec2(radius, 0).Rotated(startAngle));
            return;
        }

        var ratio = 1 - Math.Clamp(tolerance / radius, 0, 1);
        var maxStep = 2 * Math.Acos(ratio);
        if (double.IsNaN(maxStep) || maxStep <= 1e-6) maxStep = 0.1;
        var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweepAngle) / maxStep));

        for (var i = 0; i <= steps; i++)
        {
            var a = startAngle + sweepAngle * i / steps;
            var (s, c) = Math.SinCos(a);
            into.Add(new Vec2(center.X + radius * c, center.Y + radius * s));
        }
    }

    /// <summary>
    /// SVG elliptical-arc-to. Implements the endpoint→centre parameterisation from
    /// the SVG 1.1 spec appendix, including the out-of-range radius correction.
    /// </summary>
    public static void FlattenSvgArc(Polyline into, Vec2 start, Vec2 end, double rx, double ry,
                                     double xAxisRotationDeg, bool largeArc, bool sweep,
                                     double tolerance = DefaultTolerance)
    {
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx < 1e-12 || ry < 1e-12 || start.DistanceSquaredTo(end) < 1e-18)
        {
            into.Add(end);
            return;
        }

        var phi = xAxisRotationDeg * Math.PI / 180;
        var (sinPhi, cosPhi) = Math.SinCos(phi);

        var dx2 = (start.X - end.X) / 2;
        var dy2 = (start.Y - end.Y) / 2;
        var x1p = cosPhi * dx2 + sinPhi * dy2;
        var y1p = -sinPhi * dx2 + cosPhi * dy2;

        // Scale the radii up if they are too small to span the endpoints.
        var lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
        if (lambda > 1)
        {
            var s = Math.Sqrt(lambda);
            rx *= s;
            ry *= s;
        }

        var sign = largeArc != sweep ? 1.0 : -1.0;
        var num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
        var den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
        var coef = sign * Math.Sqrt(Math.Max(0, num / Math.Max(den, 1e-18)));

        var cxp = coef * rx * y1p / ry;
        var cyp = -coef * ry * x1p / rx;
        var cx = cosPhi * cxp - sinPhi * cyp + (start.X + end.X) / 2;
        var cy = sinPhi * cxp + cosPhi * cyp + (start.Y + end.Y) / 2;

        double Angle(double ux, double uy, double vx, double vy)
        {
            var dot = ux * vx + uy * vy;
            var len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
            var a = Math.Acos(Math.Clamp(len > 0 ? dot / len : 1, -1, 1));
            return ux * vy - uy * vx < 0 ? -a : a;
        }

        var ux0 = (x1p - cxp) / rx;
        var uy0 = (y1p - cyp) / ry;
        var ux1 = (-x1p - cxp) / rx;
        var uy1 = (-y1p - cyp) / ry;

        var theta1 = Angle(1, 0, ux0, uy0);
        var delta = Angle(ux0, uy0, ux1, uy1);
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        else if (sweep && delta < 0) delta += 2 * Math.PI;

        // Tolerance-driven step count using the larger radius as the worst case.
        var rMax = Math.Max(rx, ry);
        var maxStep = 2 * Math.Acos(1 - Math.Clamp(tolerance / rMax, 0, 1));
        if (double.IsNaN(maxStep) || maxStep <= 1e-6) maxStep = 0.1;
        var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(delta) / maxStep));

        for (var i = 1; i <= steps; i++)
        {
            var t = theta1 + delta * i / steps;
            var (st, ct) = Math.SinCos(t);
            var x = cosPhi * rx * ct - sinPhi * ry * st + cx;
            var y = sinPhi * rx * ct + cosPhi * ry * st + cy;
            into.Add(new Vec2(x, y));
        }
    }

    internal static double DistanceToLineSquared(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        var lenSq = ab.LengthSquared;
        if (lenSq < 1e-18) return p.DistanceSquaredTo(a);
        var t = Math.Clamp((p - a).Dot(ab) / lenSq, 0, 1);
        return p.DistanceSquaredTo(a + ab * t);
    }
}
