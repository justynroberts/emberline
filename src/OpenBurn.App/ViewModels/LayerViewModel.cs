using CommunityToolkit.Mvvm.ComponentModel;
using OpenBurn.Core.Documents;

namespace OpenBurn.App.ViewModels;

/// <summary>
/// A layer row. Wraps the model rather than duplicating it, so an edit in the
/// panel is immediately visible to the CAM pipeline with no synchronisation step
/// to forget.
/// </summary>
public sealed partial class LayerViewModel : ObservableObject
{
    private readonly Action _changed;

    public LayerViewModel(Layer layer, Action changed)
    {
        Layer = layer;
        _changed = changed;
    }

    public Layer Layer { get; }

    public string Name
    {
        get => Layer.Name;
        set { Layer.Name = value; OnPropertyChanged(); _changed(); }
    }

    public OperationKind Operation
    {
        get => Layer.Operation;
        set
        {
            Layer.Operation = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFill));
            OnPropertyChanged(nameof(Summary));
            _changed();
        }
    }

    public double SpeedMmMin
    {
        get => Layer.SpeedMmMin;
        set { Layer.SpeedMmMin = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); _changed(); }
    }

    public double PowerPercent
    {
        get => Layer.PowerPercent;
        set { Layer.PowerPercent = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); _changed(); }
    }

    public double MinPowerPercent
    {
        get => Layer.MinPowerPercent;
        set { Layer.MinPowerPercent = value; OnPropertyChanged(); _changed(); }
    }

    public int Passes
    {
        get => Layer.Passes;
        set { Layer.Passes = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); _changed(); }
    }

    public double LineIntervalMm
    {
        get => Layer.LineIntervalMm;
        set { Layer.LineIntervalMm = value; OnPropertyChanged(); OnPropertyChanged(nameof(DpiText)); _changed(); }
    }

    public bool AirAssist
    {
        get => Layer.AirAssist;
        set { Layer.AirAssist = value; OnPropertyChanged(); _changed(); }
    }

    /// <summary>
    /// Merge shapes that overlap into their combined outline before cutting.
    /// See <see cref="Layer.MergeOverlaps"/> for why this is on by default.
    /// </summary>
    public bool MergeOverlaps
    {
        get => Layer.MergeOverlaps;
        set { Layer.MergeOverlaps = value; OnPropertyChanged(); _changed(); }
    }

    public bool Enabled
    {
        get => Layer.Enabled;
        set { Layer.Enabled = value; OnPropertyChanged(); _changed(); }
    }

    public double FillAngleDeg
    {
        get => Layer.FillAngleDeg;
        set { Layer.FillAngleDeg = value; OnPropertyChanged(); _changed(); }
    }

    public FillStrategy FillStrategy
    {
        get => Layer.FillStrategy;
        set { Layer.FillStrategy = value; OnPropertyChanged(); _changed(); }
    }

    public string ColorHex => Layer.ColorHex;

    public bool IsFill => Layer.Operation == OperationKind.Fill;

    public string DpiText => $"{Layer.DpiEquivalent:0} DPI";

    public string Summary => $"{Layer.SpeedMmMin:0} mm/min · {Layer.PowerPercent:0.#}% · ×{Layer.Passes}";

    public static IReadOnlyList<OperationKind> Operations { get; } = Enum.GetValues<OperationKind>();
    public static IReadOnlyList<FillStrategy> FillStrategies { get; } = Enum.GetValues<FillStrategy>();
}
