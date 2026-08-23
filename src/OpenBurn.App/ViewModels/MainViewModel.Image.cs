using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using OpenBurn.Cam.Import;
using OpenBurn.Cam.Raster;
using OpenBurn.Core.Documents;

namespace OpenBurn.App.ViewModels;

/// <summary>
/// Photo-engraving controls for a selected bitmap.
///
/// The source image is never modified — every adjustment is applied at CAM time,
/// so brightness can be pushed around all afternoon without progressively
/// destroying the picture, and the original is still there when it turns out the
/// first setting was right.
///
/// Invert is the one people come looking for. Engraving burns dark pixels, so a
/// photograph meant for white-on-black — laser-marked slate, anodised aluminium,
/// painted tiles — has to be inverted or it comes out as a negative.
/// </summary>
public sealed partial class MainViewModel
{
    private const int PreviewMaxPx = 320;

    public bool IsImageSelected => PrimarySelection is ImageShape;

    public ImageShape? SelectedImage => PrimarySelection as ImageShape;

    private ImageAdjustments Adjustments => SelectedImage?.Adjustments ?? ImageAdjustments.Default;

    public bool ImageInvert
    {
        get => Adjustments.Invert;
        set => Adjust(a => a with { Invert = value });
    }

    public double ImageBrightness
    {
        get => Adjustments.Brightness;
        set => Adjust(a => a with { Brightness = Math.Round(value) });
    }

    public double ImageContrast
    {
        get => Adjustments.Contrast;
        set => Adjust(a => a with { Contrast = Math.Round(value) });
    }

    public double ImageGamma
    {
        get => Adjustments.Gamma;
        set => Adjust(a => a with { Gamma = Math.Clamp(Math.Round(value, 2), 0.1, 5) });
    }

    public double ImageSharpen
    {
        get => Adjustments.Sharpen;
        set => Adjust(a => a with { Sharpen = Math.Clamp(Math.Round(value, 2), 0, 2) });
    }

    public double ImageWhiteClip
    {
        get => Adjustments.WhiteClip;
        set => Adjust(a => a with { WhiteClip = Math.Clamp((int)Math.Round(value), 0, 255) });
    }

    public double ImageBlackClip
    {
        get => Adjustments.BlackClip;
        set => Adjust(a => a with { BlackClip = Math.Clamp((int)Math.Round(value), 0, 255) });
    }

    public IReadOnlyList<GreyscaleMode> GreyscaleModes { get; } = Enum.GetValues<GreyscaleMode>();

    public GreyscaleMode ImageGreyscaleMode
    {
        get => Adjustments.Mode;
        set => Adjust(a => a with { Mode = value });
    }

    public bool ImageIsAdjusted => !Adjustments.IsDefault;

    // ------------------------------------------------------------- dithering

    public IReadOnlyList<DitherInfo> DitherKinds { get; } = Dither.Catalogue;

    private DitherInfo _selectedDither = Dither.Catalogue.First(d => d.Algorithm == DitherAlgorithm.FloydSteinberg);

    /// <summary>
    /// How greyscale becomes laser pulses. Job-wide rather than per-image: it
    /// depends on the material under the beam, not on the picture.
    /// </summary>
    public DitherInfo SelectedDither
    {
        get => _selectedDither;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedDither)) return;
            _selectedDither = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DitherHint));
            QueueRegenerate();
        }
    }

    public string DitherHint => _selectedDither.Hint;

    public Dither.Options DitherOptions => Dither.Options.Default with { Algorithm = _selectedDither.Algorithm };

    // --------------------------------------------------------------- preview

    private Bitmap? _imagePreview;

    /// <summary>The selected image as it will be engraved, before dithering.</summary>
    public Bitmap? ImagePreview => _imagePreview;

    [RelayCommand]
    private void ResetImageAdjustments()
    {
        if (SelectedImage is not { } image) return;
        image.Adjustments = ImageAdjustments.Default;
        AfterImageChange();
    }

    private void Adjust(Func<ImageAdjustments, ImageAdjustments> change)
    {
        if (SelectedImage is not { } image) return;

        var updated = change(image.Adjustments);
        if (updated == image.Adjustments) return;

        image.Adjustments = updated;
        AfterImageChange();
    }

    private void AfterImageChange()
    {
        RebuildImagePreview();
        RaiseImageState();
        QueueRegenerate();
    }

    /// <summary>
    /// Rebuild the preview from a reduced copy. Applying the adjustments to a
    /// twelve-megapixel original on every drag of a slider is the difference
    /// between a control that responds and one that stutters.
    /// </summary>
    private void RebuildImagePreview()
    {
        _imagePreview = null;

        if (SelectedImage is not { } image)
        {
            OnPropertyChanged(nameof(ImagePreview));
            return;
        }

        try
        {
            var source = image.Source;
            var longest = Math.Max(source.Width, source.Height);
            if (longest > PreviewMaxPx)
            {
                var scale = PreviewMaxPx / (double)longest;
                source = ImageProcessor.Resample(source,
                    Math.Max(1, (int)Math.Round(source.Width * scale)),
                    Math.Max(1, (int)Math.Round(source.Height * scale)));
            }

            var adjusted = ImageProcessor.Apply(source, image.Adjustments);
            using var stream = new MemoryStream(ImageImporter.ToPng(adjusted));
            _imagePreview = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Console.AppendError($"Could not build the image preview: {ex.Message}");
        }

        OnPropertyChanged(nameof(ImagePreview));
    }

    private void RaiseImageState()
    {
        OnPropertyChanged(nameof(ImageInvert));
        OnPropertyChanged(nameof(ImageBrightness));
        OnPropertyChanged(nameof(ImageContrast));
        OnPropertyChanged(nameof(ImageGamma));
        OnPropertyChanged(nameof(ImageSharpen));
        OnPropertyChanged(nameof(ImageWhiteClip));
        OnPropertyChanged(nameof(ImageBlackClip));
        OnPropertyChanged(nameof(ImageGreyscaleMode));
        OnPropertyChanged(nameof(ImageIsAdjusted));
    }

    /// <summary>Called when the selection changes, so the panel follows it.</summary>
    private void RaiseImageSelection()
    {
        OnPropertyChanged(nameof(IsImageSelected));
        OnPropertyChanged(nameof(SelectedImage));
        RebuildImagePreview();
        RaiseImageState();
    }
}
