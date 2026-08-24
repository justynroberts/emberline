using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OpenBurn.Core.Documents;

/// <summary>
/// An operation layer. Geometry is assigned to a layer, and the layer carries the
/// machine settings for it — the LightBurn model, which turns out to be the right
/// one because the same outline can be a score on card and a cut on plywood.
/// </summary>
public sealed class Layer : INotifyPropertyChanged
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    private string _name = "Layer";
    private OperationKind _operation = OperationKind.Engrave;
    private double _speedMmMin = 3000;
    private double _powerPercent = 25;
    private double _minPowerPercent;
    private int _passes = 1;
    private double _lineIntervalMm = 0.1;
    private bool _airAssist;
    private bool _enabled = true;
    private int _order;
    private string _colorHex = "#FF6B2C";
    private FillStrategy _fillStrategy = FillStrategy.Hatch;
    private double _fillAngleDeg;
    private bool _bidirectional = true;
    private double _overscanMm = 2;
    private double _zOffsetMm;

    public string Name { get => _name; set => Set(ref _name, value); }
    public OperationKind Operation { get => _operation; set => Set(ref _operation, value); }

    /// <summary>Feed rate in mm/min.</summary>
    public double SpeedMmMin { get => _speedMmMin; set => Set(ref _speedMmMin, Math.Max(1, value)); }

    /// <summary>Peak power, 0–100 %. Mapped to the machine's max S value at G-code time.</summary>
    public double PowerPercent { get => _powerPercent; set => Set(ref _powerPercent, Math.Clamp(value, 0, 100)); }

    /// <summary>Floor power for dynamic/greyscale modulation, 0–100 %.</summary>
    public double MinPowerPercent { get => _minPowerPercent; set => Set(ref _minPowerPercent, Math.Clamp(value, 0, 100)); }

    public int Passes { get => _passes; set => Set(ref _passes, Math.Max(1, value)); }

    /// <summary>Distance between scan lines for Fill and raster, in millimetres.</summary>
    public double LineIntervalMm { get => _lineIntervalMm; set => Set(ref _lineIntervalMm, Math.Clamp(value, 0.005, 5)); }

    public bool AirAssist { get => _airAssist; set => Set(ref _airAssist, value); }

    private bool _mergeOverlaps = true;

    /// <summary>
    /// Merge shapes that overlap into their combined outline before cutting.
    ///
    /// On by default, because cutting overlapping shapes one at a time runs the
    /// beam through the middle of the finished piece. Holes are unaffected: a
    /// shape entirely inside another is a counter or a washer centre, and stays
    /// one. Turn it off when the crossing lines are the point — a grid scored over
    /// a panel, or a design meant to be cut into separate overlapping pieces.
    /// </summary>
    public bool MergeOverlaps { get => _mergeOverlaps; set => Set(ref _mergeOverlaps, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>Output order. Lower runs first; engraves before cuts is the usual arrangement.</summary>
    public int Order { get => _order; set => Set(ref _order, value); }

    public string ColorHex { get => _colorHex; set => Set(ref _colorHex, value); }
    public FillStrategy FillStrategy { get => _fillStrategy; set => Set(ref _fillStrategy, value); }
    public double FillAngleDeg { get => _fillAngleDeg; set => Set(ref _fillAngleDeg, value); }
    public bool Bidirectional { get => _bidirectional; set => Set(ref _bidirectional, value); }
    public double OverscanMm { get => _overscanMm; set => Set(ref _overscanMm, Math.Max(0, value)); }

    /// <summary>Per-layer Z offset for machines with a controllable Z. Zero on most diode lasers.</summary>
    public double ZOffsetMm { get => _zOffsetMm; set => Set(ref _zOffsetMm, value); }

    public double DpiEquivalent => Units.UnitConvert.IntervalToDpi(LineIntervalMm);

    public static Layer CreateDefault(OperationKind kind, int order)
    {
        var (speed, power, passes) = OperationDefaults.For(kind);
        return new Layer
        {
            Name = kind.ToString(),
            Operation = kind,
            SpeedMmMin = speed,
            PowerPercent = power,
            Passes = passes,
            Order = order,
            ColorHex = kind switch
            {
                OperationKind.Cut => "#E5484D",
                OperationKind.Score => "#F5A524",
                OperationKind.Fill => "#8B5CF6",
                _ => "#22C3D6",
            },
        };
    }

    public Layer Clone() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = Name,
        Operation = Operation,
        SpeedMmMin = SpeedMmMin,
        PowerPercent = PowerPercent,
        MinPowerPercent = MinPowerPercent,
        Passes = Passes,
        LineIntervalMm = LineIntervalMm,
        AirAssist = AirAssist,
        Enabled = Enabled,
        Order = Order,
        ColorHex = ColorHex,
        FillStrategy = FillStrategy,
        FillAngleDeg = FillAngleDeg,
        Bidirectional = Bidirectional,
        OverscanMm = OverscanMm,
        ZOffsetMm = ZOffsetMm,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
