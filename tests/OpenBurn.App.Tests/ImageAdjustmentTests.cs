using Avalonia.Headless.XUnit;
using OpenBurn.App.ViewModels;
using OpenBurn.Cam.Raster;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;
using OpenBurn.Core.Machines;
using OpenBurn.Core.Storage;
using Xunit;

namespace OpenBurn.App.Tests;

/// <summary>
/// Photo-engraving adjustments on a selected bitmap.
///
/// The engine has always applied these; nothing in the interface set them, so
/// every imported image engraved with defaults. Invert is the one that matters
/// most: without it a picture meant for slate or anodised aluminium comes out as
/// a negative.
/// </summary>
public class ImageAdjustmentTests
{
    private static MainViewModel CreateShell()
    {
        AppPaths.OverrideRoot(Path.Combine(Path.GetTempPath(), "openburn-tests", Guid.NewGuid().ToString("N")));
        AppPaths.EnsureCreated();
        return new MainViewModel(AppSettings.Default);
    }

    /// <summary>A gradient, so an inversion is unmistakable in the pixels.</summary>
    private static ImageShape Photo(int size = 40)
    {
        var px = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++) px[y * size + x] = (byte)(x * 255 / Math.Max(1, size - 1));
        }
        return new ImageShape(new RasterImage(size, size, px), 50, 50) { Name = "photo" };
    }

    private static MainViewModel ShellWithImage(out ImageShape image)
    {
        var shell = CreateShell();
        image = Photo();
        shell.Design.AddShape(image, shell.Design.Layers[0]);
        shell.SetSelection([image], additive: false);
        return shell;
    }

    [AvaloniaFact]
    public void ThePanelOnlyAppearsForABitmap()
    {
        var shell = CreateShell();
        Assert.False(shell.IsImageSelected);

        var vector = new PathShape([new Polyline([new Vec2(0, 0), new Vec2(10, 10)])]);
        shell.Design.AddShape(vector, shell.Design.Layers[0]);
        shell.SetSelection([vector], additive: false);
        Assert.False(shell.IsImageSelected);

        var shell2 = ShellWithImage(out _);
        Assert.True(shell2.IsImageSelected);
    }

    [AvaloniaFact]
    public void InvertingActuallyInvertsWhatWillBeEngraved()
    {
        var shell = ShellWithImage(out var image);

        var before = ImageProcessor.Apply(image.Source, image.Adjustments);
        shell.ImageInvert = true;
        var after = ImageProcessor.Apply(image.Source, image.Adjustments);

        Assert.True(shell.ImageInvert);
        for (var i = 0; i < before.Pixels.Length; i++)
        {
            Assert.Equal(255 - before.Pixels[i], after.Pixels[i]);
        }
    }

    [AvaloniaFact]
    public void TheOriginalPictureIsNeverTouched()
    {
        // Every adjustment happens at CAM time, so the source survives any amount
        // of fiddling and the first setting is always recoverable.
        var shell = ShellWithImage(out var image);
        var original = (byte[])image.Source.Pixels.Clone();

        shell.ImageInvert = true;
        shell.ImageBrightness = 60;
        shell.ImageContrast = -40;
        shell.ImageGamma = 2.2;

        Assert.Equal(original, image.Source.Pixels);
    }

    [AvaloniaFact]
    public void EverySliderReachesTheAdjustmentsTheCamPipelineReads()
    {
        var shell = ShellWithImage(out var image);

        shell.ImageBrightness = 25;
        shell.ImageContrast = -15;
        shell.ImageGamma = 1.8;
        shell.ImageSharpen = 0.5;
        shell.ImageWhiteClip = 240;
        shell.ImageBlackClip = 20;
        shell.ImageGreyscaleMode = GreyscaleMode.Lightness;

        var a = image.Adjustments;
        Assert.Equal(25, a.Brightness);
        Assert.Equal(-15, a.Contrast);
        Assert.Equal(1.8, a.Gamma);
        Assert.Equal(0.5, a.Sharpen);
        Assert.Equal(240, a.WhiteClip);
        Assert.Equal(20, a.BlackClip);
        Assert.Equal(GreyscaleMode.Lightness, a.Mode);
    }

    [AvaloniaFact]
    public void ValuesAreHeldInsideWhatTheEngineAccepts()
    {
        var shell = ShellWithImage(out var image);

        shell.ImageGamma = 99;
        shell.ImageSharpen = -5;
        shell.ImageWhiteClip = 9000;

        Assert.InRange(image.Adjustments.Gamma, 0.1, 5);
        Assert.InRange(image.Adjustments.Sharpen, 0, 2);
        Assert.InRange(image.Adjustments.WhiteClip, 0, 255);
    }

    [AvaloniaFact]
    public void ResetPutsItAllBack()
    {
        var shell = ShellWithImage(out var image);

        shell.ImageInvert = true;
        shell.ImageBrightness = 40;
        Assert.True(shell.ImageIsAdjusted);

        shell.ResetImageAdjustmentsCommand.Execute(null);

        Assert.False(shell.ImageIsAdjusted);
        Assert.True(image.Adjustments.IsDefault);
    }

    [AvaloniaFact]
    public void ThePreviewShowsTheAdjustedPictureAndFollowsTheSelection()
    {
        var shell = ShellWithImage(out _);
        Assert.NotNull(shell.ImagePreview);

        var first = shell.ImagePreview;
        shell.ImageInvert = true;
        Assert.NotSame(first, shell.ImagePreview);

        shell.SetSelection([], additive: false);
        Assert.Null(shell.ImagePreview);
    }

    [AvaloniaFact]
    public void TheChosenDitheringReachesTheJob()
    {
        var shell = ShellWithImage(out _);

        shell.SelectedDither = Dither.Catalogue.First(d => d.Algorithm == DitherAlgorithm.Atkinson);

        Assert.Equal(DitherAlgorithm.Atkinson, shell.DitherOptions.Algorithm);
        Assert.Contains("plywood", shell.DitherHint, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void AnInvertedImageGeneratesADifferentJob()
    {
        // The end of the chain: the setting has to change the G-code, not just a
        // property on a view model.
        var shell = ShellWithImage(out _);
        shell.Design.Layers[0].Operation = OperationKind.Engrave;

        shell.RegenerateNow();
        var plain = shell.GcodeText;

        shell.ImageInvert = true;
        shell.RegenerateNow();
        var inverted = shell.GcodeText;

        Assert.NotEqual(plain, inverted);
    }
}
