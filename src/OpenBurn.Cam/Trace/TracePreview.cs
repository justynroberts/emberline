using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;
using SkiaSharp;

namespace OpenBurn.Cam.Trace;

/// <summary>
/// Renders a trace over the image it came from, as PNG bytes.
///
/// The background is left transparent and the source is drawn as neutral grey, so
/// one image reads correctly against a light panel or a dark one. Getting a
/// second, theme-aware copy of this to render correctly was not worth it.
/// </summary>
public static class TracePreview
{
    public const uint DefaultPathColour = 0xFFE0621Fu;   // ember, between the light and dark tokens

    /// <summary>
    /// Draw the greyscale source, faintly, with the traced paths over it.
    /// Sized so the longest side is <paramref name="maxDimension"/> device pixels.
    /// </summary>
    public static byte[] Render(
        RasterImage source,
        IReadOnlyList<Polyline> contours,
        int maxDimension = 640,
        bool showSource = true,
        uint pathColour = DefaultPathColour)
    {
        var scale = Math.Min(1.0, maxDimension / (double)Math.Max(source.Width, source.Height));
        if (scale <= 0 || double.IsNaN(scale)) scale = 1;

        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var surface = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(surface);
        canvas.Clear(SKColors.Transparent);

        if (showSource) DrawGhost(canvas, source, w, h);

        var stroke = new SKPaint
        {
            Color = new SKColor(pathColour),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.4f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };

        using (stroke)
        {
            var builder = new SKPathBuilder();
            foreach (var contour in contours)
            {
                if (contour.Count < 2) continue;

                builder.MoveTo((float)(contour[0].X * scale), (float)(contour[0].Y * scale));
                for (var i = 1; i < contour.Count; i++)
                {
                    builder.LineTo((float)(contour[i].X * scale), (float)(contour[i].Y * scale));
                }
                if (contour.IsClosed) builder.Close();
            }

            using var path = builder.Detach();
            canvas.DrawPath(path, stroke);
        }

        canvas.Flush();

        using var image = SKImage.FromBitmap(surface);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    /// <summary>
    /// The source image as translucent grey — dark pixels become more opaque.
    /// Grey rather than black so it stays visible on a dark panel.
    /// </summary>
    private static void DrawGhost(SKCanvas canvas, RasterImage source, int w, int h)
    {
        var rgba = new byte[source.Width * source.Height * 4];
        for (var i = 0; i < source.Pixels.Length; i++)
        {
            var ink = (byte)((255 - source.Pixels[i]) * 0.42);
            var o = i * 4;
            // Premultiplied, so the grey has to be scaled by its own alpha.
            var level = (byte)(ink * 145 / 255);
            rgba[o] = level;
            rgba[o + 1] = level;
            rgba[o + 2] = level;
            rgba[o + 3] = ink;
        }

        using var ghost = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, ghost.GetPixels(), rgba.Length);

        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(ghost, new SKRect(0, 0, w, h),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
    }
}
