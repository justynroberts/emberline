using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenBurn.Cam.Trace;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;

namespace OpenBurn.App.ViewModels;

/// <summary>
/// The trace dialog: turn a bitmap into paths, with the controls that decide how.
///
/// Every setting re-traces and redraws, because the only way to know whether a
/// threshold is right is to look at what it produces. The re-trace is debounced
/// rather than run per keystroke — dragging the threshold slider across a
/// megapixel image would otherwise queue up a hundred full traces.
/// </summary>
public sealed partial class TraceViewModel : ObservableObject
{
    private readonly RasterImage _source;
    private readonly DispatcherTimer? _debounce;
    private bool _loaded;

    public TraceViewModel(RasterImage source, string sourceName, double widthMm, double heightMm, bool useTimer = true)
    {
        _source = source;
        SourceName = sourceName;
        WidthMm = widthMm;
        HeightMm = heightMm;

        _threshold = BitmapTracer.AutoThreshold(source);

        if (useTimer)
        {
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                Retrace();
            };
        }

        _loaded = true;
        Retrace();
    }

    public string SourceName { get; }
    public double WidthMm { get; }
    public double HeightMm { get; }

    /// <summary>
    /// Where the image already sits, when the trace came from a placed shape. The
    /// trace has to land exactly on top of it — anything else and the operator
    /// lines up artwork against a preview that is not where the burn will be.
    /// Null when tracing a file that was never imported.
    /// </summary>
    public Matrix2D? SourceTransform { get; init; }

    public string SourceSummary =>
        $"{_source.Width} × {_source.Height} px, placed at {WidthMm:0.#} × {HeightMm:0.#} mm";

    // ------------------------------------------------------------- settings

    [ObservableProperty] private int _threshold;
    [ObservableProperty] private bool _centreline;
    [ObservableProperty] private bool _invert;
    [ObservableProperty] private bool _despeckle = true;
    [ObservableProperty] private double _simplify = 0.8;
    [ObservableProperty] private int _smoothPasses = 2;
    [ObservableProperty] private int _minimumArea = 12;
    [ObservableProperty] private bool _showSource = true;

    partial void OnThresholdChanged(int value) => QueueRetrace();
    partial void OnCentrelineChanged(bool value) => QueueRetrace();
    partial void OnInvertChanged(bool value) => QueueRetrace();
    partial void OnDespeckleChanged(bool value) => QueueRetrace();
    partial void OnSimplifyChanged(double value) => QueueRetrace();
    partial void OnSmoothPassesChanged(int value) => QueueRetrace();
    partial void OnMinimumAreaChanged(int value) => QueueRetrace();
    partial void OnShowSourceChanged(bool value) => QueueRetrace();

    // -------------------------------------------------------------- results

    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private bool _isBusy;

    public bool HasNote => !string.IsNullOrEmpty(Note);

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    /// <summary>The paths from the most recent trace, in image pixel coordinates.</summary>
    public IReadOnlyList<Polyline> Contours { get; private set; } = [];

    public TraceMode Mode => Centreline ? TraceMode.Centreline : TraceMode.Outline;

    public TraceOptions Options => new()
    {
        Threshold = Threshold,
        Mode = Mode,
        SimplifyTolerancePx = Simplify,
        SmoothPasses = SmoothPasses,
        MinimumAreaPx = MinimumArea,
        Despeckle = Despeckle,
        Invert = Invert,
    };

    private void QueueRetrace()
    {
        if (!_loaded) return;

        if (_debounce is null)
        {
            Retrace();
            return;
        }

        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Re-trace and re-render now, skipping the debounce.</summary>
    public void Retrace()
    {
        _debounce?.Stop();
        IsBusy = true;

        try
        {
            var result = BitmapTracer.Trace(_source, Options);
            Contours = result.Contours;

            var png = TracePreview.Render(_source, result.Contours, 720, ShowSource);
            using var stream = new MemoryStream(png);
            Preview = new Bitmap(stream);

            var kind = result.Mode == TraceMode.Centreline ? "stroke" : "outline";
            Summary = result.ContourCount == 0
                ? "Nothing found at this threshold."
                : $"{result.ContourCount:N0} {kind}{(result.ContourCount == 1 ? "" : "s")}, " +
                  $"{result.PointCount:N0} points, about {PathLengthMm(result.Contours):N0} mm of travel.";

            Note = string.Join("  ", result.Notes);
        }
        catch (Exception ex)
        {
            Contours = [];
            Summary = "Trace failed.";
            Note = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void AutoThreshold()
    {
        Threshold = BitmapTracer.AutoThreshold(_source);
        Retrace();
    }

    [RelayCommand]
    private void Reset()
    {
        _loaded = false;
        Centreline = false;
        Invert = false;
        Despeckle = true;
        Simplify = 0.8;
        SmoothPasses = 2;
        MinimumArea = 12;
        ShowSource = true;
        Threshold = BitmapTracer.AutoThreshold(_source);
        _loaded = true;
        Retrace();
    }

    /// <summary>The traced paths as a shape on the bed, or null if nothing was found.</summary>
    public PathShape? BuildShape()
    {
        if (Contours.Count == 0) return null;

        var result = new TraceResult(Contours, 0, Mode);
        var shape = BitmapTracer.ToShape(result, _source.Width, _source.Height, WidthMm, HeightMm,
            $"{SourceName} (traced)");
        return shape.Paths.Count == 0 ? null : shape;
    }

    private double PathLengthMm(IReadOnlyList<Polyline> contours)
    {
        var perPixel = WidthMm / Math.Max(1, _source.Width);
        var total = 0.0;
        foreach (var c in contours) total += c.Length;
        return total * perPixel;
    }
}
