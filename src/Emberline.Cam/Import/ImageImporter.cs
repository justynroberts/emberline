using Emberline.Core.Documents;
using SkiaSharp;

namespace Emberline.Cam.Import;

public sealed record ImageImportResult(
    RasterImage Image,
    double SuggestedWidthMm,
    double SuggestedHeightMm,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Decoding for PNG, JPEG, BMP, GIF and WebP, via Skia.
///
/// This is the only place in the CAM layer that knows an image format exists.
/// Everything downstream works on <see cref="RasterImage"/>, which is a width, a
/// height and one byte per pixel — so the whole raster pipeline can be tested
/// from a hand-built array with no image files involved.
/// </summary>
public static class ImageImporter
{
    /// <summary>Assumed resolution when a file carries no physical size.</summary>
    public const double DefaultDpi = 96.0;

    public static ImageImportResult Load(string path, GreyscaleMode mode = GreyscaleMode.Luma)
    {
        using var stream = File.OpenRead(path);
        return Load(stream, mode);
    }

    public static ImageImportResult Load(Stream stream, GreyscaleMode mode = GreyscaleMode.Luma)
    {
        var warnings = new List<string>();

        using var bitmap = SKBitmap.Decode(stream)
            ?? throw new InvalidDataException("This image could not be decoded. Supported formats are PNG, JPEG, BMP, GIF and WebP.");

        // Normalise to straight (non-premultiplied) RGBA so the alpha composite in
        // RasterImage.FromRgba is correct. Premultiplied data would darken the
        // edges of anything with a soft alpha channel.
        using var normalised = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (!bitmap.CopyTo(normalised, SKColorType.Rgba8888))
        {
            throw new InvalidDataException("This image could not be converted to a format Emberline can engrave.");
        }

        var pixels = normalised.GetPixelSpan();
        var image = RasterImage.FromRgba(pixels, normalised.Width, normalised.Height, mode);

        if (image.Width * (long)image.Height > 40_000_000)
        {
            warnings.Add("This image is very large. It will be resampled down to the engraving grid, so a smaller source would import faster with no loss of detail.");
        }

        var widthMm = image.Width * 25.4 / DefaultDpi;
        var heightMm = image.Height * 25.4 / DefaultDpi;

        return new ImageImportResult(image, widthMm, heightMm, warnings);
    }

    /// <summary>Load and place at a real-world size, keeping the source aspect ratio.</summary>
    public static ImageShape LoadAsShape(string path, double? widthMm = null, double? heightMm = null)
    {
        var result = Load(path);
        var aspect = (double)result.Image.Width / result.Image.Height;

        double w, h;
        if (widthMm is { } ww && heightMm is { } hh) { w = ww; h = hh; }
        else if (widthMm is { } w2) { w = w2; h = w2 / aspect; }
        else if (heightMm is { } h2) { h = h2; w = h2 * aspect; }
        else { w = result.SuggestedWidthMm; h = result.SuggestedHeightMm; }

        return new ImageShape(result.Image, w, h)
        {
            Name = Path.GetFileNameWithoutExtension(path),
            SourcePath = path,
        };
    }

    /// <summary>Render a greyscale image to PNG bytes, for previews and job thumbnails.</summary>
    public static byte[] ToPng(RasterImage image)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var rgba = image.ToRgba();
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);

        using var skImage = SKImage.FromBitmap(bitmap);
        using var data = skImage.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    public static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
