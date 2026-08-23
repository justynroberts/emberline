using OpenBurn.Core.Documents;

namespace OpenBurn.Cam.Raster;

/// <summary>
/// The adjustment chain that runs before dithering.
///
/// Everything is done through a 256-entry lookup table rather than per-pixel
/// arithmetic. A twenty-megapixel photograph is twenty million pixels; building
/// the curve once and indexing into it is roughly an order of magnitude faster
/// than evaluating a gamma power function per pixel, which is what makes the
/// live preview keep up with a dragged slider.
/// </summary>
public static class ImageProcessor
{
    public static byte[] BuildLookupTable(ImageAdjustments adj)
    {
        var lut = new byte[256];

        // The legacy Photoshop contrast curve — familiar behaviour beats a
        // technically superior curve nobody can predict.
        var c = Math.Clamp(adj.Contrast, -100, 100);
        var factor = 259 * (c + 255) / (255 * (259 - c));
        var invGamma = 1.0 / Math.Max(0.01, adj.Gamma);
        var brightnessShift = adj.Brightness / 100.0 * 255.0;

        for (var i = 0; i < 256; i++)
        {
            var v = factor * (i - 128) + 128;
            v += brightnessShift;
            v = Math.Clamp(v, 0, 255);
            v = 255 * Math.Pow(v / 255.0, invGamma);

            if (adj.Invert) v = 255 - v;
            if (adj.BlackClip > 0 && v <= adj.BlackClip) v = 0;
            if (adj.WhiteClip < 255 && v >= adj.WhiteClip) v = 255;

            lut[i] = (byte)Math.Clamp(Math.Round(v), 0, 255);
        }

        return lut;
    }

    public static RasterImage Apply(RasterImage source, ImageAdjustments adj)
    {
        var result = source;

        if (adj.Sharpen > 0) result = UnsharpMask(result, adj.Sharpen);

        var lut = BuildLookupTable(adj);
        var pixels = new byte[result.Pixels.Length];
        var src = result.Pixels;
        for (var i = 0; i < pixels.Length; i++) pixels[i] = lut[src[i]];

        return new RasterImage(result.Width, result.Height, pixels);
    }

    /// <summary>
    /// Resample to a target pixel size.
    ///
    /// Box-averaging when shrinking, which is the normal case — a 4000 px photo
    /// going to 800 engraving lines. Point or bilinear downsampling aliases badly,
    /// and aliasing in a dithered engrave does not look like detail, it looks like
    /// dirt burned into the workpiece.
    /// </summary>
    public static RasterImage Resample(RasterImage source, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == source.Width && height == source.Height) return source.Clone();

        return width < source.Width || height < source.Height
            ? BoxResample(source, width, height)
            : BilinearResample(source, width, height);
    }

    private static RasterImage BoxResample(RasterImage src, int width, int height)
    {
        var outPixels = new byte[width * height];
        var xRatio = (double)src.Width / width;
        var yRatio = (double)src.Height / height;
        var sp = src.Pixels;

        for (var y = 0; y < height; y++)
        {
            var sy0 = (int)(y * yRatio);
            var sy1 = Math.Max(sy0 + 1, Math.Min(src.Height, (int)Math.Ceiling((y + 1) * yRatio)));

            for (var x = 0; x < width; x++)
            {
                var sx0 = (int)(x * xRatio);
                var sx1 = Math.Max(sx0 + 1, Math.Min(src.Width, (int)Math.Ceiling((x + 1) * xRatio)));

                var sum = 0;
                var count = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    var row = sy * src.Width;
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        sum += sp[row + sx];
                        count++;
                    }
                }
                outPixels[y * width + x] = count > 0 ? (byte)(sum / count) : (byte)255;
            }
        }

        return new RasterImage(width, height, outPixels);
    }

    private static RasterImage BilinearResample(RasterImage src, int width, int height)
    {
        var outPixels = new byte[width * height];
        var xRatio = width > 1 ? (double)(src.Width - 1) / (width - 1) : 0;
        var yRatio = height > 1 ? (double)(src.Height - 1) / (height - 1) : 0;
        var sp = src.Pixels;

        for (var y = 0; y < height; y++)
        {
            var fy = y * yRatio;
            var y0 = (int)fy;
            var y1 = Math.Min(src.Height - 1, y0 + 1);
            var wy = fy - y0;

            for (var x = 0; x < width; x++)
            {
                var fx = x * xRatio;
                var x0 = (int)fx;
                var x1 = Math.Min(src.Width - 1, x0 + 1);
                var wx = fx - x0;

                var a = sp[y0 * src.Width + x0];
                var b = sp[y0 * src.Width + x1];
                var c = sp[y1 * src.Width + x0];
                var d = sp[y1 * src.Width + x1];

                var value = a * (1 - wx) * (1 - wy) + b * wx * (1 - wy) + c * (1 - wx) * wy + d * wx * wy;
                outPixels[y * width + x] = (byte)Math.Clamp(Math.Round(value), 0, 255);
            }
        }

        return new RasterImage(width, height, outPixels);
    }

    /// <summary>
    /// Unsharp mask. Photo engraving loses local contrast because the burn spreads
    /// slightly beyond the beam, so a little sharpening before dithering visibly
    /// improves the result on wood and leather.
    /// </summary>
    public static RasterImage UnsharpMask(RasterImage source, double amount, int radius = 1)
    {
        var blurred = BoxBlur(source, radius);
        var pixels = new byte[source.Pixels.Length];
        var src = source.Pixels;
        var blur = blurred.Pixels;

        for (var i = 0; i < pixels.Length; i++)
        {
            var value = src[i] + amount * (src[i] - blur[i]);
            pixels[i] = (byte)Math.Clamp(Math.Round(value), 0, 255);
        }

        return new RasterImage(source.Width, source.Height, pixels);
    }

    /// <summary>Separable box blur — two O(n) passes rather than one O(n·r²).</summary>
    public static RasterImage BoxBlur(RasterImage source, int radius)
    {
        if (radius < 1) return source.Clone();

        var w = source.Width;
        var h = source.Height;
        var temp = new byte[w * h];
        var result = new byte[w * h];
        var src = source.Pixels;
        var window = radius * 2 + 1;

        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                var sum = 0;
                for (var k = -radius; k <= radius; k++) sum += src[row + Math.Clamp(x + k, 0, w - 1)];
                temp[row + x] = (byte)(sum / window);
            }
        }

        for (var x = 0; x < w; x++)
        {
            for (var y = 0; y < h; y++)
            {
                var sum = 0;
                for (var k = -radius; k <= radius; k++) sum += temp[Math.Clamp(y + k, 0, h - 1) * w + x];
                result[y * w + x] = (byte)(sum / window);
            }
        }

        return new RasterImage(w, h, result);
    }

    /// <summary>Rotate by a multiple of 90°, the only rotation that needs no resampling.</summary>
    public static RasterImage Rotate90(RasterImage source, int quarterTurns)
    {
        var turns = ((quarterTurns % 4) + 4) % 4;
        if (turns == 0) return source.Clone();

        var w = source.Width;
        var h = source.Height;
        var src = source.Pixels;

        if (turns == 2)
        {
            var flipped = new byte[src.Length];
            for (var i = 0; i < src.Length; i++) flipped[src.Length - 1 - i] = src[i];
            return new RasterImage(w, h, flipped);
        }

        var nw = h;
        var nh = w;
        var outPixels = new byte[src.Length];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = src[y * w + x];
                if (turns == 1) outPixels[x * nw + (nw - 1 - y)] = v;
                else outPixels[(nh - 1 - x) * nw + y] = v;
            }
        }

        return new RasterImage(nw, nh, outPixels);
    }

    /// <summary>Transpose, so a vertical scan can reuse the horizontal row walker.</summary>
    public static RasterImage Transpose(RasterImage source)
    {
        var w = source.Width;
        var h = source.Height;
        var src = source.Pixels;
        var outPixels = new byte[src.Length];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++) outPixels[x * h + y] = src[y * w + x];
        }

        return new RasterImage(h, w, outPixels);
    }

    /// <summary>Rotate about the centre into a larger white-padded bitmap, for free-angle scanning.</summary>
    public static (RasterImage Image, double CentreX, double CentreY) RotateFree(RasterImage source, double degrees)
    {
        var rad = degrees * Math.PI / 180;
        var (sin, cos) = Math.SinCos(rad);
        var w = source.Width;
        var h = source.Height;

        var nw = (int)Math.Ceiling(Math.Abs(w * cos) + Math.Abs(h * sin));
        var nh = (int)Math.Ceiling(Math.Abs(w * sin) + Math.Abs(h * cos));
        var outPixels = new byte[nw * nh];
        Array.Fill(outPixels, (byte)255);

        var cx = w / 2.0;
        var cy = h / 2.0;
        var ncx = nw / 2.0;
        var ncy = nh / 2.0;
        var src = source.Pixels;

        for (var y = 0; y < nh; y++)
        {
            for (var x = 0; x < nw; x++)
            {
                // Inverse-rotate the destination pixel back into source space.
                var dx = x - ncx;
                var dy = y - ncy;
                var sxf = cos * dx + sin * dy + cx;
                var syf = -sin * dx + cos * dy + cy;

                if (sxf < 0 || syf < 0 || sxf >= w - 1 || syf >= h - 1) continue;

                var x0 = (int)sxf;
                var y0 = (int)syf;
                var fx = sxf - x0;
                var fy = syf - y0;

                var a = src[y0 * w + x0];
                var b = src[y0 * w + x0 + 1];
                var c = src[(y0 + 1) * w + x0];
                var d = src[(y0 + 1) * w + x0 + 1];

                var value = a * (1 - fx) * (1 - fy) + b * fx * (1 - fy) + c * (1 - fx) * fy + d * fx * fy;
                outPixels[y * nw + x] = (byte)Math.Clamp(Math.Round(value), 0, 255);
            }
        }

        return (new RasterImage(nw, nh, outPixels), ncx, ncy);
    }
}
