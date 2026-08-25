using Emberline.Core.Geometry;

namespace Emberline.Core.Documents;

/// <summary>Photo-engraving adjustments applied before dithering.</summary>
public sealed record ImageAdjustments
{
    public GreyscaleMode Mode { get; init; } = GreyscaleMode.Luma;

    /// <summary>-100 … 100.</summary>
    public double Brightness { get; init; }

    /// <summary>-100 … 100.</summary>
    public double Contrast { get; init; }

    /// <summary>0.1 … 5. One means no change.</summary>
    public double Gamma { get; init; } = 1.0;

    public bool Invert { get; init; }

    /// <summary>Pixels this light or lighter become pure white and are skipped. 255 disables.</summary>
    public int WhiteClip { get; init; } = 255;

    /// <summary>Pixels this dark or darker become pure black. 0 disables.</summary>
    public int BlackClip { get; init; }

    /// <summary>Unsharp-mask strength, 0 … 2. Photo engraving benefits from a little.</summary>
    public double Sharpen { get; init; }

    public static readonly ImageAdjustments Default = new();

    public bool IsDefault =>
        Mode == GreyscaleMode.Luma && Brightness == 0 && Contrast == 0 && Gamma == 1.0 &&
        !Invert && WhiteClip == 255 && BlackClip == 0 && Sharpen == 0;
}

/// <summary>
/// A bitmap placed on the bed. The source image is kept at full resolution and
/// never modified — adjustments and resampling happen at CAM time, so the user can
/// keep tweaking brightness without progressively destroying the picture.
/// </summary>
public sealed class ImageShape : Shape
{
    private ImageAdjustments _adjustments = ImageAdjustments.Default;
    private double _widthMm;
    private double _heightMm;

    public ImageShape(RasterImage source, double widthMm, double heightMm)
    {
        Source = source;
        _widthMm = widthMm;
        _heightMm = heightMm;
        Name = "Image";
    }

    public RasterImage Source { get; }

    /// <summary>Where the file came from, for the job library and for re-linking.</summary>
    public string? SourcePath { get; init; }

    public ImageAdjustments Adjustments
    {
        get => _adjustments;
        set { _adjustments = value; OnChanged(); }
    }

    /// <summary>Placed size in millimetres, before <see cref="Shape.Transform"/>.</summary>
    public double WidthMm
    {
        get => _widthMm;
        set { _widthMm = Math.Max(0.1, value); OnChanged(); }
    }

    public double HeightMm
    {
        get => _heightMm;
        set { _heightMm = Math.Max(0.1, value); OnChanged(); }
    }

    public double AspectRatio => Source.Width / (double)Source.Height;

    public override Rect2 LocalBounds => new(0, 0, _widthMm, _heightMm);

    /// <summary>The frame, not the content — used for selection, framing and bounds checks.</summary>
    public override IReadOnlyList<Polyline> GetOutlines(double tolerance = Curves.DefaultTolerance)
    {
        var b = LocalBounds;
        var p = new Polyline(
        [
            Transform.Apply(b.MinX, b.MinY),
            Transform.Apply(b.MaxX, b.MinY),
            Transform.Apply(b.MaxX, b.MaxY),
            Transform.Apply(b.MinX, b.MaxY),
        ], closed: true);
        return [p];
    }

    public override Shape Clone()
    {
        var copy = new ImageShape(Source, _widthMm, _heightMm)
        {
            SourcePath = SourcePath,
            Adjustments = _adjustments,
        };
        CopyBaseTo(copy);
        return copy;
    }
}
