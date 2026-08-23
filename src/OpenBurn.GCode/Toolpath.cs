using OpenBurn.Core.Geometry;

namespace OpenBurn.GCode;

/// <summary>
/// A flat, typed-array toolpath.
///
/// One object per segment costs well over a hundred bytes once the runtime is
/// done with it, so a million-line raster job would allocate hundreds of megabytes
/// of garbage before the preview drew a single pixel. Struct-of-arrays keeps it to
/// about 25 bytes a segment and is also exactly the layout the renderer wants to
/// walk.
/// </summary>
public sealed class Toolpath
{
    private double[] _x0 = new double[4096];
    private double[] _y0 = new double[4096];
    private double[] _x1 = new double[4096];
    private double[] _y1 = new double[4096];
    private float[] _power = new float[4096];
    private float[] _feed = new float[4096];
    private bool[] _rapid = new bool[4096];
    private int[] _sourceLine = new int[4096];

    public int Count { get; private set; }

    public ReadOnlySpan<double> X0 => _x0.AsSpan(0, Count);
    public ReadOnlySpan<double> Y0 => _y0.AsSpan(0, Count);
    public ReadOnlySpan<double> X1 => _x1.AsSpan(0, Count);
    public ReadOnlySpan<double> Y1 => _y1.AsSpan(0, Count);

    /// <summary>Normalised 0–1 laser power. Rapids and laser-off moves are always 0.</summary>
    public ReadOnlySpan<float> Power => _power.AsSpan(0, Count);

    /// <summary>mm/min.</summary>
    public ReadOnlySpan<float> Feed => _feed.AsSpan(0, Count);

    public ReadOnlySpan<bool> Rapid => _rapid.AsSpan(0, Count);

    /// <summary>Index of the G-code line that produced this segment.</summary>
    public ReadOnlySpan<int> SourceLine => _sourceLine.AsSpan(0, Count);

    public Rect2 Bounds { get; private set; } = Rect2.Empty;
    public double CutLengthMm { get; private set; }
    public double TravelLengthMm { get; private set; }

    /// <summary>Lines the interpreter could not make sense of, with a reason.</summary>
    public List<ToolpathWarning> Warnings { get; } = [];

    public bool UsesLaser { get; internal set; }
    public double MaxSpindleSeen { get; internal set; }
    public bool IsInches { get; internal set; }

    internal void Add(double x0, double y0, double x1, double y1, float power, float feed, bool rapid, int sourceLine)
    {
        if (Count == _x0.Length) Grow();

        _x0[Count] = x0;
        _y0[Count] = y0;
        _x1[Count] = x1;
        _y1[Count] = y1;
        _power[Count] = power;
        _feed[Count] = feed;
        _rapid[Count] = rapid;
        _sourceLine[Count] = sourceLine;
        Count++;

        var length = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        if (power > 0) CutLengthMm += length;
        else TravelLengthMm += length;

        Bounds = Bounds.Add(x1, y1);
    }

    internal void SeedBounds(double x, double y) => Bounds = Bounds.Add(x, y);

    private void Grow()
    {
        var capacity = _x0.Length * 2;
        Array.Resize(ref _x0, capacity);
        Array.Resize(ref _y0, capacity);
        Array.Resize(ref _x1, capacity);
        Array.Resize(ref _y1, capacity);
        Array.Resize(ref _power, capacity);
        Array.Resize(ref _feed, capacity);
        Array.Resize(ref _rapid, capacity);
        Array.Resize(ref _sourceLine, capacity);
    }

    /// <summary>Bounds of burning moves only. Framing should trace what will be burned, not the rapids.</summary>
    public Rect2 BurnBounds
    {
        get
        {
            var r = Rect2.Empty;
            for (var i = 0; i < Count; i++)
            {
                if (_power[i] <= 0) continue;
                r = r.Add(_x0[i], _y0[i]).Add(_x1[i], _y1[i]);
            }
            return r;
        }
    }

    /// <summary>Every burning-move endpoint, for convex-hull framing.</summary>
    public IEnumerable<Vec2> BurnPoints()
    {
        for (var i = 0; i < Count; i++)
        {
            if (_power[i] <= 0) continue;
            yield return new Vec2(_x0[i], _y0[i]);
            yield return new Vec2(_x1[i], _y1[i]);
        }
    }
}

public sealed record ToolpathWarning(int LineIndex, string Text);
