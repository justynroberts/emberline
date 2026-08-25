namespace Emberline.Core.Documents;

/// <summary>
/// An 8-bit greyscale bitmap. 0 is black (maximum burn), 255 is white (no burn).
///
/// Core deliberately knows nothing about PNG, JPEG or SkiaSharp — decoding lives
/// in the layer that already has an imaging dependency, and hands the result down
/// as plain bytes. That keeps every algorithm in Cam testable from a byte array.
/// </summary>
public sealed class RasterImage
{
    public RasterImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        if (pixels.Length != width * height)
        {
            throw new ArgumentException($"Expected {width * height} pixels, got {pixels.Length}.", nameof(pixels));
        }
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major, one byte per pixel. Row 0 is the top of the picture.</summary>
    public byte[] Pixels { get; }

    public byte this[int x, int y] => Pixels[y * Width + x];

    public static RasterImage Create(int width, int height, byte fill = 255)
    {
        var px = new byte[width * height];
        if (fill != 0) Array.Fill(px, fill);
        return new RasterImage(width, height, px);
    }

    /// <summary>
    /// Convert 8-bit-per-channel RGBA to greyscale, compositing alpha over white.
    /// Transparent means "leave the material alone", which is what a user expects
    /// when they drop a PNG with a cut-out background onto the bed.
    /// </summary>
    public static RasterImage FromRgba(ReadOnlySpan<byte> rgba, int width, int height, GreyscaleMode mode = GreyscaleMode.Luma)
    {
        var px = new byte[width * height];
        for (int i = 0, p = 0; i < px.Length; i++, p += 4)
        {
            var a = rgba[p + 3] / 255.0;
            var r = rgba[p] * a + 255 * (1 - a);
            var g = rgba[p + 1] * a + 255 * (1 - a);
            var b = rgba[p + 2] * a + 255 * (1 - a);
            px[i] = (byte)Math.Clamp(Channel(r, g, b, mode), 0, 255);
        }
        return new RasterImage(width, height, px);
    }

    private static double Channel(double r, double g, double b, GreyscaleMode mode) => mode switch
    {
        GreyscaleMode.Average => (r + g + b) / 3,
        GreyscaleMode.Lightness => (Math.Max(r, Math.Max(g, b)) + Math.Min(r, Math.Min(g, b))) / 2,
        GreyscaleMode.Red => r,
        GreyscaleMode.Green => g,
        GreyscaleMode.Blue => b,
        GreyscaleMode.Max => Math.Max(r, Math.Max(g, b)),
        GreyscaleMode.Min => Math.Min(r, Math.Min(g, b)),
        _ => 0.2126 * r + 0.7152 * g + 0.0722 * b,
    };

    public RasterImage Clone() => new(Width, Height, (byte[])Pixels.Clone());

    /// <summary>Expand back to RGBA for on-screen preview.</summary>
    public byte[] ToRgba()
    {
        var outBuf = new byte[Width * Height * 4];
        for (int i = 0, p = 0; i < Pixels.Length; i++, p += 4)
        {
            var v = Pixels[i];
            outBuf[p] = v;
            outBuf[p + 1] = v;
            outBuf[p + 2] = v;
            outBuf[p + 3] = 255;
        }
        return outBuf;
    }

    public int[] Histogram()
    {
        var h = new int[256];
        foreach (var p in Pixels) h[p]++;
        return h;
    }
}

public enum GreyscaleMode
{
    Luma,
    Average,
    Lightness,
    Red,
    Green,
    Blue,
    Max,
    Min,
}
