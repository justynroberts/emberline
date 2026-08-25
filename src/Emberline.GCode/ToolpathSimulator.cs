using Emberline.Core.Geometry;

namespace Emberline.GCode;

/// <summary>Where the head is, and what it is doing, at one instant.</summary>
public readonly record struct SimulationState(
    Vec2 Position,
    int SegmentIndex,
    double PowerFraction,
    double FeedMmMin,
    bool IsRapid,
    TimeSpan Elapsed)
{
    public static readonly SimulationState Start = new(Vec2.Zero, 0, 0, 0, true, TimeSpan.Zero);
}

/// <summary>
/// Replays a toolpath against the clock.
///
/// The PRD asks for a visual preview that runs Start → Rapid → Engrave → Cut →
/// Finish, and for the operator to be able to see it before committing a
/// workpiece. Doing that honestly means using the same acceleration-aware timing
/// as the estimate: a simulation that plays every segment at equal speed makes a
/// dense raster look instant and a long rapid look slow, which is exactly backwards.
///
/// Segment durations are computed once, then a cumulative table turns any time
/// into a position with a binary search — so scrubbing a two-hour job is as cheap
/// as playing it.
/// </summary>
public sealed class ToolpathSimulator
{
    private readonly Toolpath _toolpath;
    private readonly double[] _cumulative;
    private readonly double[] _durations;

    public ToolpathSimulator(Toolpath toolpath, MachineLimits? limits = null)
    {
        _toolpath = toolpath;
        var l = limits ?? MachineLimits.Default;

        _durations = new double[toolpath.Count];
        _cumulative = new double[toolpath.Count + 1];

        // Reuse the estimator's per-segment model so the simulation and the
        // estimate can never disagree with each other.
        var perSegment = TimeEstimator.SegmentDurations(toolpath, l);
        for (var i = 0; i < toolpath.Count; i++)
        {
            _durations[i] = perSegment[i];
            _cumulative[i + 1] = _cumulative[i] + perSegment[i];
        }

        Total = TimeSpan.FromSeconds(_cumulative[^1]);
    }

    public TimeSpan Total { get; }
    public int SegmentCount => _toolpath.Count;

    /// <summary>Where the head is at <paramref name="elapsed"/> into the job.</summary>
    public SimulationState At(TimeSpan elapsed)
    {
        if (_toolpath.Count == 0) return SimulationState.Start;

        var seconds = Math.Clamp(elapsed.TotalSeconds, 0, _cumulative[^1]);

        // The end of the job is handled explicitly, and compared against Total
        // rather than the raw sum. Two reasons: interpolating to t=1 leaves the head
        // a nanometre short of the endpoint, and TimeSpan only resolves to 100 ns,
        // so a caller passing `simulator.Total` back in lands just before the true
        // end and would otherwise never reach it.
        if (elapsed >= Total || seconds >= _cumulative[^1] - 1e-6)
        {
            var last = _toolpath.Count - 1;
            return new SimulationState(
                new Vec2(_toolpath.X1[last], _toolpath.Y1[last]),
                last,
                _toolpath.Power[last],
                _toolpath.Feed[last],
                _toolpath.Rapid[last],
                Total);
        }

        // Binary search for the segment containing this instant.
        var index = Array.BinarySearch(_cumulative, seconds);
        if (index < 0) index = ~index - 1;
        index = Math.Clamp(index, 0, _toolpath.Count - 1);

        var duration = _durations[index];
        var into = seconds - _cumulative[index];
        var t = duration > 1e-9 ? Math.Clamp(into / duration, 0, 1) : 1;

        var x = _toolpath.X0[index] + (_toolpath.X1[index] - _toolpath.X0[index]) * t;
        var y = _toolpath.Y0[index] + (_toolpath.Y1[index] - _toolpath.Y0[index]) * t;

        return new SimulationState(
            new Vec2(x, y),
            index,
            _toolpath.Power[index],
            _toolpath.Feed[index],
            _toolpath.Rapid[index],
            TimeSpan.FromSeconds(seconds));
    }

    /// <summary>Where the head is at a fraction of the way through, 0 to 1.</summary>
    public SimulationState AtFraction(double fraction) =>
        At(TimeSpan.FromSeconds(Math.Clamp(fraction, 0, 1) * _cumulative[^1]));

    /// <summary>
    /// Fraction of the job completed by <paramref name="segmentIndex"/>. Lets the
    /// canvas dim what has not been cut yet while a real job streams.
    /// </summary>
    public double FractionAtSegment(int segmentIndex)
    {
        if (_toolpath.Count == 0 || _cumulative[^1] <= 0) return 0;
        var index = Math.Clamp(segmentIndex, 0, _toolpath.Count);
        return _cumulative[index] / _cumulative[^1];
    }

    /// <summary>A short description of what the head is doing, for the readout.</summary>
    public static string Describe(SimulationState state) => state switch
    {
        { IsRapid: true } => "Rapid",
        { PowerFraction: >= 0.75 } => $"Cut · {state.PowerFraction * 100:0}%",
        { PowerFraction: > 0 } => $"Engrave · {state.PowerFraction * 100:0}%",
        _ => "Travel",
    };
}
