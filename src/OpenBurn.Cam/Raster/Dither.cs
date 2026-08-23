using OpenBurn.Core.Documents;

namespace OpenBurn.Cam.Raster;

public enum DitherAlgorithm
{
    /// <summary>No dithering — power follows pixel brightness directly.</summary>
    Greyscale,
    Threshold,
    FloydSteinberg,
    Jarvis,
    Stucki,
    Atkinson,
    Sierra3,
    SierraLite,
    Burkes,
    Bayer4,
    Bayer8,
    Random,
}

public sealed record DitherInfo(DitherAlgorithm Algorithm, string Label, string Hint);

/// <summary>
/// Dithering kernels.
///
/// At engraving speed a diode laser is close to a one-bit device: a spot is either
/// burned or it is not. Greyscale power modulation works on anodised aluminium and
/// slate, where the tonal response is roughly linear, but on wood and leather it is
/// so non-linear that error-diffusion dithering produces a visibly better
/// photograph. Which kernel looks best is genuinely material-dependent, which is
/// why there are ten of them rather than one.
/// </summary>
public static class Dither
{
    public static readonly IReadOnlyList<DitherInfo> Catalogue =
    [
        new(DitherAlgorithm.Greyscale, "Greyscale", "No dithering — power follows pixel brightness. Best on anodised aluminium and slate."),
        new(DitherAlgorithm.FloydSteinberg, "Floyd–Steinberg", "The default. Sharp, even, works on almost everything."),
        new(DitherAlgorithm.Jarvis, "Jarvis–Judice–Ninke", "Softer and smoother than Floyd. Good for faces and leather."),
        new(DitherAlgorithm.Stucki, "Stucki", "Like Jarvis but crisper. Strong on metal marking."),
        new(DitherAlgorithm.Atkinson, "Atkinson", "Lifts contrast and drops detail in flat areas. Flattering on light plywood."),
        new(DitherAlgorithm.Sierra3, "Sierra-3", "A balanced middle ground between Floyd and Jarvis."),
        new(DitherAlgorithm.SierraLite, "Sierra Lite", "Fast, slightly grainier. Fine on large work."),
        new(DitherAlgorithm.Burkes, "Burkes", "Wide diffusion, low grain. Good on card and paper."),
        new(DitherAlgorithm.Bayer4, "Ordered 4×4", "Visible crosshatch texture. Deliberately retro."),
        new(DitherAlgorithm.Bayer8, "Ordered 8×8", "Finer ordered pattern; very consistent burn density."),
        new(DitherAlgorithm.Random, "Random threshold", "Noisy and organic. Hides banding on smooth gradients."),
        new(DitherAlgorithm.Threshold, "Hard threshold", "Pure black and white. For line art and logos, not photographs."),
    ];

    private readonly record struct Tap(int Dx, int Dy, int Weight);
    private readonly record struct Kernel(int Divisor, Tap[] Taps);

    private static readonly Dictionary<DitherAlgorithm, Kernel> Kernels = new()
    {
        [DitherAlgorithm.FloydSteinberg] = new(16,
        [
            new(1, 0, 7),
            new(-1, 1, 3), new(0, 1, 5), new(1, 1, 1),
        ]),

        [DitherAlgorithm.Jarvis] = new(48,
        [
            new(1, 0, 7), new(2, 0, 5),
            new(-2, 1, 3), new(-1, 1, 5), new(0, 1, 7), new(1, 1, 5), new(2, 1, 3),
            new(-2, 2, 1), new(-1, 2, 3), new(0, 2, 5), new(1, 2, 3), new(2, 2, 1),
        ]),

        [DitherAlgorithm.Stucki] = new(42,
        [
            new(1, 0, 8), new(2, 0, 4),
            new(-2, 1, 2), new(-1, 1, 4), new(0, 1, 8), new(1, 1, 4), new(2, 1, 2),
            new(-2, 2, 1), new(-1, 2, 2), new(0, 2, 4), new(1, 2, 2), new(2, 2, 1),
        ]),

        [DitherAlgorithm.Atkinson] = new(8,
        [
            new(1, 0, 1), new(2, 0, 1),
            new(-1, 1, 1), new(0, 1, 1), new(1, 1, 1),
            new(0, 2, 1),
        ]),

        [DitherAlgorithm.Sierra3] = new(32,
        [
            new(1, 0, 5), new(2, 0, 3),
            new(-2, 1, 2), new(-1, 1, 4), new(0, 1, 5), new(1, 1, 4), new(2, 1, 2),
            new(-1, 2, 2), new(0, 2, 3), new(1, 2, 2),
        ]),

        [DitherAlgorithm.SierraLite] = new(4,
        [
            new(1, 0, 2),
            new(-1, 1, 1), new(0, 1, 1),
        ]),

        [DitherAlgorithm.Burkes] = new(32,
        [
            new(1, 0, 8), new(2, 0, 4),
            new(-2, 1, 2), new(-1, 1, 4), new(0, 1, 8), new(1, 1, 4), new(2, 1, 2),
        ]),
    };

    private static readonly int[,] Bayer4 =
    {
        { 0, 8, 2, 10 },
        { 12, 4, 14, 6 },
        { 3, 11, 1, 9 },
        { 15, 7, 13, 5 },
    };

    private static readonly int[,] Bayer8 = BuildBayer8();

    private static int[,] BuildBayer8()
    {
        var m = new int[8, 8];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var q = Bayer4[y % 4, x % 4];
                var quadrant = (y < 4 ? 0 : 2) + (x < 4 ? 0 : 1);
                m[y, x] = q * 4 + quadrant switch { 0 => 0, 1 => 2, 2 => 3, _ => 1 };
            }
        }
        return m;
    }

    public sealed record Options
    {
        public DitherAlgorithm Algorithm { get; init; } = DitherAlgorithm.FloydSteinberg;

        /// <summary>Cut point for threshold-style algorithms, 0–255.</summary>
        public int Threshold { get; init; } = 128;

        /// <summary>Serpentine scanning halves the directional artefacts error diffusion produces.</summary>
        public bool Serpentine { get; init; } = true;

        /// <summary>Seed for the random algorithm. Fixed so output stays reproducible.</summary>
        public int Seed { get; init; } = 0x5EED;

        public static readonly Options Default = new();
    }

    /// <summary>
    /// Produce the image the engraver will actually burn. Error-diffusion and
    /// ordered algorithms return pure 0 or 255; <see cref="DitherAlgorithm.Greyscale"/>
    /// passes the image through untouched for power modulation.
    /// </summary>
    public static RasterImage Apply(RasterImage source, Options? options = null)
    {
        var o = options ?? Options.Default;
        var w = source.Width;
        var h = source.Height;
        var src = source.Pixels;
        var result = new byte[src.Length];

        switch (o.Algorithm)
        {
            case DitherAlgorithm.Greyscale:
                Array.Copy(src, result, src.Length);
                return new RasterImage(w, h, result);

            case DitherAlgorithm.Threshold:
                for (var i = 0; i < src.Length; i++) result[i] = src[i] < o.Threshold ? (byte)0 : (byte)255;
                return new RasterImage(w, h, result);

            case DitherAlgorithm.Bayer4:
            case DitherAlgorithm.Bayer8:
            {
                var size = o.Algorithm == DitherAlgorithm.Bayer4 ? 4 : 8;
                var scale = size * size;
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        var cell = o.Algorithm == DitherAlgorithm.Bayer4 ? Bayer4[y % 4, x % 4] : Bayer8[y % 8, x % 8];
                        var t = (cell + 0.5) / scale * 255;
                        result[y * w + x] = src[y * w + x] < t ? (byte)0 : (byte)255;
                    }
                }
                return new RasterImage(w, h, result);
            }

            case DitherAlgorithm.Random:
            {
                // A fixed-seed PRNG, not Random.Shared: identical inputs must give
                // byte-identical G-code or the compatibility tests are meaningless.
                var state = (uint)o.Seed;
                for (var i = 0; i < src.Length; i++)
                {
                    result[i] = src[i] < NextUnit(ref state) * 255 ? (byte)0 : (byte)255;
                }
                return new RasterImage(w, h, result);
            }
        }

        if (!Kernels.TryGetValue(o.Algorithm, out var kernel))
        {
            Array.Copy(src, result, src.Length);
            return new RasterImage(w, h, result);
        }

        // Error diffusion needs somewhere with headroom to carry fractional error;
        // accumulating into bytes throws away the very thing being diffused.
        var buffer = new float[src.Length];
        for (var i = 0; i < src.Length; i++) buffer[i] = src[i];

        for (var y = 0; y < h; y++)
        {
            var rightward = !o.Serpentine || y % 2 == 0;

            for (var k = 0; k < w; k++)
            {
                var x = rightward ? k : w - 1 - k;
                var index = y * w + x;

                var old = buffer[index];
                var quantised = old < o.Threshold ? (byte)0 : (byte)255;
                result[index] = quantised;

                var error = old - quantised;
                if (error == 0) continue;

                foreach (var tap in kernel.Taps)
                {
                    var sx = x + (rightward ? tap.Dx : -tap.Dx);
                    var sy = y + tap.Dy;
                    if (sx < 0 || sx >= w || sy >= h) continue;
                    buffer[sy * w + sx] += error * tap.Weight / kernel.Divisor;
                }
            }
        }

        return new RasterImage(w, h, result);
    }

    /// <summary>Mulberry32 — small, fast, and fully deterministic from its seed.</summary>
    private static double NextUnit(ref uint state)
    {
        state += 0x6D2B79F5;
        var t = state;
        t = (uint)((t ^ (t >> 15)) * (t | 1));
        t ^= t + (uint)((t ^ (t >> 7)) * (t | 61));
        return ((t ^ (t >> 14)) & 0xFFFFFFFF) / 4294967296.0;
    }
}
