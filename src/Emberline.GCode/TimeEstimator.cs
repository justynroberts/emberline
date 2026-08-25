namespace Emberline.GCode;

public sealed record MachineLimits
{
    /// <summary>mm/min.</summary>
    public double MaxRateX { get; init; } = 12000;
    public double MaxRateY { get; init; } = 12000;

    /// <summary>mm/sec².</summary>
    public double AccelerationX { get; init; } = 1500;
    public double AccelerationY { get; init; } = 1500;

    /// <summary>GRBL's $11, in millimetres.</summary>
    public double JunctionDeviation { get; init; } = 0.01;

    /// <summary>Fixed per-line cost in seconds — link latency and planner work.</summary>
    public double PerLineOverheadSeconds { get; init; }

    public static readonly MachineLimits Default = new();

    public static MachineLimits FromSettings(IReadOnlyDictionary<int, double> settings, MachineLimits? fallback = null)
    {
        var f = fallback ?? Default;
        return new MachineLimits
        {
            MaxRateX = settings.TryGetValue(110, out var mx) ? mx : f.MaxRateX,
            MaxRateY = settings.TryGetValue(111, out var my) ? my : f.MaxRateY,
            AccelerationX = settings.TryGetValue(120, out var ax) ? ax : f.AccelerationX,
            AccelerationY = settings.TryGetValue(121, out var ay) ? ay : f.AccelerationY,
            JunctionDeviation = settings.TryGetValue(11, out var jd) ? jd : f.JunctionDeviation,
            PerLineOverheadSeconds = f.PerLineOverheadSeconds,
        };
    }
}

public readonly record struct TimeEstimate(
    TimeSpan Total,
    TimeSpan Cutting,
    TimeSpan Travelling,
    double CutLengthMm,
    double TravelLengthMm,
    int Segments)
{
    public static readonly TimeEstimate Zero = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0);
}

/// <summary>
/// Acceleration-aware job time estimation.
///
/// Dividing path length by feed rate is wrong by two to five times on raster work,
/// because a raster job is tens of thousands of very short moves that never get
/// anywhere near their commanded feed. This does what the motion planner does:
/// derive a junction speed for every corner from the turn angle, run a backward
/// then a forward acceleration pass, and integrate each segment's trapezoidal
/// velocity profile.
/// </summary>
public static class TimeEstimator
{
    public static TimeEstimate Estimate(Toolpath tp, MachineLimits? limits = null)
    {
        var l = limits ?? MachineLimits.Default;
        if (tp.Count == 0) return TimeEstimate.Zero;

        var durations = SegmentDurations(tp, l);

        var cutting = 0.0;
        var travelling = 0.0;
        for (var i = 0; i < tp.Count; i++)
        {
            if (tp.Rapid[i]) travelling += durations[i];
            else cutting += durations[i];
        }

        return new TimeEstimate(
            TimeSpan.FromSeconds(cutting + travelling),
            TimeSpan.FromSeconds(cutting),
            TimeSpan.FromSeconds(travelling),
            tp.CutLengthMm,
            tp.TravelLengthMm,
            tp.Count);
    }

    /// <summary>
    /// Time for each individual segment, in seconds.
    ///
    /// Shared with the simulator so a replay and an estimate can never disagree —
    /// a preview that plays at a different speed from the prediction is worse than
    /// no preview, because it teaches the operator to distrust both.
    /// </summary>
    public static double[] SegmentDurations(Toolpath tp, MachineLimits? limits = null)
    {
        var l = limits ?? MachineLimits.Default;
        var n = tp.Count;
        if (n == 0) return [];

        var length = new double[n];
        var unitX = new double[n];
        var unitY = new double[n];
        var vMax = new double[n];      // mm/s
        var accel = new double[n];     // mm/s²

        var x0 = tp.X0;
        var y0 = tp.Y0;
        var x1 = tp.X1;
        var y1 = tp.Y1;
        var feed = tp.Feed;
        var rapid = tp.Rapid;

        for (var i = 0; i < n; i++)
        {
            var dx = x1[i] - x0[i];
            var dy = y1[i] - y0[i];
            var len = Math.Sqrt(dx * dx + dy * dy);
            length[i] = len;

            if (len > 0)
            {
                unitX[i] = dx / len;
                unitY[i] = dy / len;
            }

            // The slowest axis governs how fast the vector can move.
            var rateLimitX = Math.Abs(unitX[i]) > 1e-9 ? l.MaxRateX / Math.Abs(unitX[i]) : double.PositiveInfinity;
            var rateLimitY = Math.Abs(unitY[i]) > 1e-9 ? l.MaxRateY / Math.Abs(unitY[i]) : double.PositiveInfinity;
            var rateLimit = Math.Min(rateLimitX, rateLimitY) / 60.0;
            if (double.IsInfinity(rateLimit)) rateLimit = Math.Min(l.MaxRateX, l.MaxRateY) / 60.0;

            var commanded = rapid[i] ? rateLimit : (feed[i] > 0 ? feed[i] : 600) / 60.0;
            vMax[i] = Math.Max(0.01, Math.Min(commanded, rateLimit));

            var accelX = Math.Abs(unitX[i]) > 1e-9 ? l.AccelerationX / Math.Abs(unitX[i]) : double.PositiveInfinity;
            var accelY = Math.Abs(unitY[i]) > 1e-9 ? l.AccelerationY / Math.Abs(unitY[i]) : double.PositiveInfinity;
            accel[i] = Math.Min(accelX, accelY);
            if (double.IsInfinity(accel[i]) || accel[i] <= 0) accel[i] = Math.Min(l.AccelerationX, l.AccelerationY);
        }

        // entry[i] is the speed entering segment i; entry[n] is the final stop.
        var entry = new double[n + 1];

        for (var i = 1; i < n; i++)
        {
            if (length[i] <= 0 || length[i - 1] <= 0)
            {
                entry[i] = 0;
                continue;
            }

            // GRBL's centripetal junction model: how fast can the head round this
            // corner while staying within JunctionDeviation of the exact vertex.
            var cosTheta = -(unitX[i - 1] * unitX[i] + unitY[i - 1] * unitY[i]);
            var c = Math.Clamp(cosTheta, -0.999999, 0.999999);
            var sinHalf = Math.Sqrt((1 - c) / 2);

            var junction = sinHalf >= 0.999999
                ? 0.0 // complete reversal — must come to a stop
                : Math.Sqrt(accel[i] * l.JunctionDeviation * sinHalf / (1 - sinHalf));

            entry[i] = Math.Min(junction, Math.Min(vMax[i], vMax[i - 1]));
        }

        entry[0] = 0;
        entry[n] = 0;

        // Backward pass: guarantee we can decelerate into each junction.
        for (var i = n - 1; i >= 0; i--)
        {
            var reachable = Math.Sqrt(entry[i + 1] * entry[i + 1] + 2 * accel[i] * length[i]);
            if (entry[i] > reachable) entry[i] = reachable;
        }

        // Forward pass: guarantee we can accelerate out of each junction.
        for (var i = 0; i < n; i++)
        {
            var reachable = Math.Sqrt(entry[i] * entry[i] + 2 * accel[i] * length[i]);
            if (entry[i + 1] > reachable) entry[i + 1] = reachable;
        }

        var durations = new double[n];
        for (var i = 0; i < n; i++)
        {
            durations[i] = SegmentTime(length[i], entry[i], entry[i + 1], vMax[i], accel[i]) + l.PerLineOverheadSeconds;
        }

        return durations;
    }

    /// <summary>
    /// Time to cover <paramref name="length"/> starting at <paramref name="vIn"/>,
    /// finishing at <paramref name="vOut"/>, capped at <paramref name="vMax"/>.
    /// Handles the triangular case where there is no room to cruise.
    /// </summary>
    public static double SegmentTime(double length, double vIn, double vOut, double vMax, double accel)
    {
        if (length <= 0) return 0;
        if (accel <= 0) return length / Math.Max(vMax, 0.01);

        var peakSquared = (2 * accel * length + vIn * vIn + vOut * vOut) / 2;
        var peak = Math.Min(vMax, Math.Sqrt(Math.Max(0, peakSquared)));

        var accelDistance = Math.Max(0, (peak * peak - vIn * vIn) / (2 * accel));
        var decelDistance = Math.Max(0, (peak * peak - vOut * vOut) / (2 * accel));
        var cruiseDistance = length - accelDistance - decelDistance;

        var t = (peak - vIn) / accel + (peak - vOut) / accel;
        if (cruiseDistance > 0) t += cruiseDistance / Math.Max(peak, 0.01);

        return Math.Max(0, t);
    }

    /// <summary>Format a duration the way an operator wants to read it.</summary>
    public static string Format(TimeSpan span)
    {
        if (span < TimeSpan.Zero || span.TotalSeconds > TimeSpan.MaxValue.TotalSeconds) return "—";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes:00}m";
        if (span.TotalMinutes >= 1) return $"{span.Minutes}m {span.Seconds:00}s";
        return $"{span.Seconds}s";
    }
}
