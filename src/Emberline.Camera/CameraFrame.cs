namespace Emberline.Camera;

/// <summary>
/// One captured frame, RGBA8888, row-major, no stride padding.
///
/// A plain byte array rather than a platform image type so the vision code can be
/// tested from a hand-built array with no camera, no window and no native
/// dependency — which is what makes calibration maths testable in CI at all.
/// </summary>
public sealed class CameraFrame
{
    public CameraFrame(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (pixels.Length != width * height * 4)
        {
            throw new ArgumentException($"Expected {width * height * 4} bytes of RGBA, got {pixels.Length}.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
        CapturedAt = DateTimeOffset.UtcNow;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public DateTimeOffset CapturedAt { get; init; }

    public static CameraFrame Create(int width, int height, byte fill = 0)
    {
        var pixels = new byte[width * height * 4];
        if (fill != 0) Array.Fill(pixels, fill);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return new CameraFrame(width, height, pixels);
    }

    public (byte R, byte G, byte B, byte A) this[int x, int y]
    {
        get
        {
            var i = (y * Width + x) * 4;
            return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
        }
    }

    public void Set(int x, int y, byte r, byte g, byte b, byte a = 255)
    {
        var i = (y * Width + x) * 4;
        Pixels[i] = r;
        Pixels[i + 1] = g;
        Pixels[i + 2] = b;
        Pixels[i + 3] = a;
    }

    /// <summary>Luminance, for detection work that does not care about colour.</summary>
    public byte[] ToGreyscale()
    {
        var grey = new byte[Width * Height];
        for (int i = 0, p = 0; i < grey.Length; i++, p += 4)
        {
            grey[i] = (byte)(0.2126 * Pixels[p] + 0.7152 * Pixels[p + 1] + 0.0722 * Pixels[p + 2]);
        }
        return grey;
    }

    public CameraFrame Clone() => new(Width, Height, (byte[])Pixels.Clone()) { CapturedAt = CapturedAt };
}
