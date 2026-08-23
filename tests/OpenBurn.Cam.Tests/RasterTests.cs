using OpenBurn.Cam.Raster;
using OpenBurn.Core.Documents;
using OpenBurn.GCode;
using Xunit;

namespace OpenBurn.Cam.Tests;

public class ImageProcessorTests
{
    private static RasterImage Gradient(int w, int h)
    {
        var px = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++) px[y * w + x] = (byte)(x * 255 / Math.Max(1, w - 1));
        }
        return new RasterImage(w, h, px);
    }

    [Fact]
    public void InvertFlipsTheTonalRange()
    {
        var src = Gradient(16, 1);
        var result = ImageProcessor.Apply(src, ImageAdjustments.Default with { Invert = true });

        Assert.Equal(255, result[0, 0]);
        Assert.Equal(0, result[15, 0]);
    }

    [Fact]
    public void WhiteClipTurnsLightPixelsIntoPureWhite()
    {
        var src = Gradient(256, 1);
        var result = ImageProcessor.Apply(src, ImageAdjustments.Default with { WhiteClip = 200 });

        Assert.Equal(255, result[220, 0]);
        Assert.True(result[100, 0] < 255);
    }

    [Fact]
    public void GammaBrightensTheMidtones()
    {
        var src = Gradient(256, 1);
        var brighter = ImageProcessor.Apply(src, ImageAdjustments.Default with { Gamma = 2.0 });
        Assert.True(brighter[128, 0] > src[128, 0]);
    }

    [Fact]
    public void BoxResampleAveragesRatherThanDroppingPixels()
    {
        // Alternating black and white must average to mid grey, not pick one of them.
        var px = new byte[8];
        for (var i = 0; i < 8; i++) px[i] = i % 2 == 0 ? (byte)0 : (byte)255;
        var src = new RasterImage(8, 1, px);

        var result = ImageProcessor.Resample(src, 4, 1);
        Assert.All(result.Pixels, p => Assert.InRange(p, 120, 135));
    }

    [Fact]
    public void ResamplingToTheSameSizeIsALosslessCopy()
    {
        var src = Gradient(16, 16);
        var result = ImageProcessor.Resample(src, 16, 16);
        Assert.Equal(src.Pixels, result.Pixels);
    }

    [Fact]
    public void TransposeSwapsAxes()
    {
        var src = Gradient(4, 2);
        var t = ImageProcessor.Transpose(src);

        Assert.Equal(2, t.Width);
        Assert.Equal(4, t.Height);
        Assert.Equal(src[3, 1], t[1, 3]);
    }

    [Fact]
    public void Rotate90ChangesOrientationAndPreservesPixelCount()
    {
        var src = Gradient(4, 2);
        var r = ImageProcessor.Rotate90(src, 1);

        Assert.Equal(2, r.Width);
        Assert.Equal(4, r.Height);
        Assert.Equal(src.Pixels.Length, r.Pixels.Length);
    }

    [Fact]
    public void FourQuarterTurnsReturnTheOriginal()
    {
        var src = Gradient(5, 3);
        var r = ImageProcessor.Rotate90(ImageProcessor.Rotate90(ImageProcessor.Rotate90(ImageProcessor.Rotate90(src, 1), 1), 1), 1);
        Assert.Equal(src.Pixels, r.Pixels);
    }
}

public class DitherTests
{
    private static RasterImage Flat(int w, int h, byte value) => new(w, h, Enumerable.Repeat(value, w * h).ToArray());

    [Theory]
    [InlineData(DitherAlgorithm.FloydSteinberg)]
    [InlineData(DitherAlgorithm.Jarvis)]
    [InlineData(DitherAlgorithm.Stucki)]
    [InlineData(DitherAlgorithm.Atkinson)]
    [InlineData(DitherAlgorithm.Sierra3)]
    [InlineData(DitherAlgorithm.SierraLite)]
    [InlineData(DitherAlgorithm.Burkes)]
    [InlineData(DitherAlgorithm.Bayer4)]
    [InlineData(DitherAlgorithm.Bayer8)]
    [InlineData(DitherAlgorithm.Random)]
    [InlineData(DitherAlgorithm.Threshold)]
    public void EveryBinarisingAlgorithmProducesOnlyBlackAndWhite(DitherAlgorithm algorithm)
    {
        var src = Flat(32, 32, 128);
        var result = Dither.Apply(src, new Dither.Options { Algorithm = algorithm });
        Assert.All(result.Pixels, p => Assert.True(p is 0 or 255, $"{algorithm} produced {p}"));
    }

    [Fact]
    public void GreyscalePassesThePictureThrough()
    {
        var src = Flat(8, 8, 100);
        var result = Dither.Apply(src, new Dither.Options { Algorithm = DitherAlgorithm.Greyscale });
        Assert.All(result.Pixels, p => Assert.Equal(100, p));
    }

    [Fact]
    public void MidGreyDithersToRoughlyHalfCoverage()
    {
        var src = Flat(64, 64, 128);
        var result = Dither.Apply(src, new Dither.Options { Algorithm = DitherAlgorithm.FloydSteinberg });

        var black = result.Pixels.Count(p => p == 0);
        var ratio = (double)black / result.Pixels.Length;
        Assert.InRange(ratio, 0.4, 0.6);
    }

    [Fact]
    public void DarkerInputProducesMoreBurnedPixels()
    {
        var light = Dither.Apply(Flat(64, 64, 200), Dither.Options.Default).Pixels.Count(p => p == 0);
        var dark = Dither.Apply(Flat(64, 64, 60), Dither.Options.Default).Pixels.Count(p => p == 0);
        Assert.True(dark > light);
    }

    [Fact]
    public void OutputIsReproducibleIncludingTheRandomAlgorithm()
    {
        // Identical input must give byte-identical output, or the compatibility
        // tests against known-good G-code are worthless.
        var src = Flat(48, 48, 140);
        foreach (var algorithm in Enum.GetValues<DitherAlgorithm>())
        {
            var a = Dither.Apply(src, new Dither.Options { Algorithm = algorithm });
            var b = Dither.Apply(src, new Dither.Options { Algorithm = algorithm });
            Assert.Equal(a.Pixels, b.Pixels);
        }
    }

    [Fact]
    public void PureWhiteStaysWhiteAndPureBlackStaysBlack()
    {
        Assert.All(Dither.Apply(Flat(16, 16, 255), Dither.Options.Default).Pixels, p => Assert.Equal(255, p));
        Assert.All(Dither.Apply(Flat(16, 16, 0), Dither.Options.Default).Pixels, p => Assert.Equal(0, p));
    }

    [Fact]
    public void TheCatalogueCoversEveryAlgorithm()
    {
        foreach (var algorithm in Enum.GetValues<DitherAlgorithm>())
        {
            Assert.Contains(Dither.Catalogue, e => e.Algorithm == algorithm);
        }
    }
}

public class RasterEngraverTests
{
    private static RasterImage Checkerboard(int size, int cell)
    {
        var px = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++) px[y * size + x] = (x / cell + y / cell) % 2 == 0 ? (byte)0 : (byte)255;
        }
        return new RasterImage(size, size, px);
    }

    [Fact]
    public void RunMergingCollapsesEqualPowerPixelsIntoOneMove()
    {
        // Ten identical black pixels must become one move, not ten. This is the
        // single biggest factor in raster file size and streaming throughput.
        var row = new byte[10];
        var runs = RasterEngraver.RowToRuns(row, 0, 1000);

        Assert.Single(runs);
        Assert.Equal(0, runs[0].Start);
        Assert.Equal(10, runs[0].End);
        Assert.Equal(1000, runs[0].Spindle);
    }

    [Fact]
    public void WhitePixelsProduceZeroPowerRuns()
    {
        byte[] row = [0, 0, 255, 255, 0];
        var runs = RasterEngraver.RowToRuns(row, 0, 1000);

        Assert.Equal(3, runs.Count);
        Assert.Equal(1000, runs[0].Spindle);
        Assert.Equal(0, runs[1].Spindle);
        Assert.Equal(1000, runs[2].Spindle);
    }

    [Fact]
    public void GreyLevelsMapLinearlyIntoThePowerRange()
    {
        byte[] row = [0, 128, 254];
        var runs = RasterEngraver.RowToRuns(row, 100, 1000);

        Assert.Equal(1000, runs[0].Spindle);
        Assert.InRange(runs[1].Spindle, 540, 570);
        Assert.InRange(runs[2].Spindle, 100, 110);
    }

    [Fact]
    public void GeneratesValidGcodeThatInterpretsBackToTheRightSize()
    {
        var image = Checkerboard(40, 4);
        var result = RasterEngraver.Generate(image, RasterOptions.Default with
        {
            WidthMm = 40,
            HeightMm = 40,
            OriginX = 10,
            OriginY = 10,
            LineIntervalMm = 1,
            OverscanMm = 0,
            MaxSpindle = 1000,
        });

        Assert.True(result.BurnMoves > 0);
        Assert.Contains("M4 S0", result.Lines);
        Assert.Contains("M5", result.Lines);

        // Round-trip through the interpreter: the burned area must land where asked.
        var tp = GcodeInterpreter.Interpret(result.Lines);
        var bounds = tp.BurnBounds;
        Assert.InRange(bounds.MinX, 9.9, 10.1);
        Assert.InRange(bounds.MaxX, 49, 50.1);
        Assert.InRange(bounds.MinY, 9.9, 11.1);
        Assert.InRange(bounds.MaxY, 49, 50.1);
    }

    [Fact]
    public void OverscanExtendsBeyondTheArtworkAtZeroPower()
    {
        var image = Checkerboard(20, 2);
        var options = RasterOptions.Default with
        {
            WidthMm = 20, HeightMm = 20, OriginX = 20, OriginY = 20,
            LineIntervalMm = 1, OverscanMm = 5,
        };

        var tp = GcodeInterpreter.Interpret(RasterEngraver.Generate(image, options).Lines);

        // Travel goes outside the artwork; burning does not.
        Assert.True(tp.Bounds.MinX < 19.5, "overscan should move outside the artwork");
        Assert.True(tp.BurnBounds.MinX >= 19.9, "no burning should happen in the overscan region");
    }

    [Fact]
    public void BlankRowsAreSkippedEntirely()
    {
        // Only the middle row has ink.
        var px = new byte[10 * 5];
        Array.Fill(px, (byte)255);
        for (var x = 0; x < 10; x++) px[2 * 10 + x] = 0;

        var result = RasterEngraver.Generate(new RasterImage(10, 5, px), RasterOptions.Default with
        {
            WidthMm = 10, HeightMm = 5, LineIntervalMm = 1, OverscanMm = 0,
        });

        Assert.Equal(1, result.Rows);
    }

    [Fact]
    public void MoreDpiMeansMorePixelsInTheEngravingGrid()
    {
        var coarse = RasterEngraver.TargetPixelSize(RasterOptions.Default with { WidthMm = 100, HeightMm = 100, LineIntervalMm = 0.2 });
        var fine = RasterEngraver.TargetPixelSize(RasterOptions.Default with { WidthMm = 100, HeightMm = 100, LineIntervalMm = 0.05 });

        Assert.Equal((500, 500), coarse);
        Assert.Equal((2000, 2000), fine);
    }

    [Fact]
    public void OutputIsDeterministic()
    {
        var image = Checkerboard(32, 3);
        var a = RasterEngraver.Generate(image, RasterOptions.Default with { WidthMm = 32, HeightMm = 32, LineIntervalMm = 1 });
        var b = RasterEngraver.Generate(image, RasterOptions.Default with { WidthMm = 32, HeightMm = 32, LineIntervalMm = 1 });
        Assert.Equal(a.Text, b.Text);
    }

    [Fact]
    public void ThePowerRangeIsRespected()
    {
        var image = Checkerboard(20, 2);
        var result = RasterEngraver.Generate(image, RasterOptions.Default with
        {
            WidthMm = 20, HeightMm = 20, LineIntervalMm = 1,
            MinPowerPercent = 20, MaxPowerPercent = 60, MaxSpindle = 1000,
        });

        var tp = GcodeInterpreter.Interpret(result.Lines, new InterpreterOptions { MaxSpindle = 1000 });
        Assert.True(tp.MaxSpindleSeen <= 600, $"S{tp.MaxSpindleSeen} exceeds the 60% ceiling");
    }

    [Fact]
    public void TheWholePipelineRunsFromAPhotographToGcode()
    {
        // A synthetic "photograph": a smooth radial gradient.
        const int size = 200;
        var px = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var d = Math.Sqrt((x - size / 2.0) * (x - size / 2.0) + (y - size / 2.0) * (y - size / 2.0));
                px[y * size + x] = (byte)Math.Clamp(d / (size / 2.0) * 255, 0, 255);
            }
        }

        var result = RasterEngraver.Process(
            new RasterImage(size, size, px),
            ImageAdjustments.Default with { Contrast = 10 },
            new Dither.Options { Algorithm = DitherAlgorithm.FloydSteinberg },
            RasterOptions.Default with { WidthMm = 50, HeightMm = 50, LineIntervalMm = 0.2, OriginX = 5, OriginY = 5 });

        Assert.True(result.Rows > 200, $"only {result.Rows} rows engraved");
        Assert.True(result.BurnMoves > 1000);

        var tp = GcodeInterpreter.Interpret(result.Lines);
        Assert.InRange(tp.BurnBounds.Width, 45, 51);
        Assert.InRange(tp.BurnBounds.Height, 45, 51);
    }
}
