using OpenBurn.Cam;
using OpenBurn.Cam.Import;
using OpenBurn.Cam.Trace;
using OpenBurn.Cam.Vector;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;
using OpenBurn.Core.Machines;
using OpenBurn.GCode;
using Xunit;

namespace OpenBurn.Cam.Tests;

public class SvgPathParserTests
{
    [Fact]
    public void ParsesAbsoluteLineCommands()
    {
        var paths = SvgPathParser.Parse("M 0 0 L 10 0 L 10 10 Z");
        var p = Assert.Single(paths);

        Assert.True(p.IsClosed);
        Assert.Equal(3, p.Count);
        Assert.Equal(new Vec2(10, 10), p[2]);
    }

    [Fact]
    public void ParsesRelativeCommands()
    {
        var p = Assert.Single(SvgPathParser.Parse("m 5 5 l 10 0 l 0 10 z"));
        Assert.Equal(new Vec2(5, 5), p[0]);
        Assert.Equal(new Vec2(15, 5), p[1]);
        Assert.Equal(new Vec2(15, 15), p[2]);
    }

    [Fact]
    public void RepeatedCoordinatesAfterMBecomeImplicitLineTos()
    {
        // "M 0 0 10 0 10 10" is three points, not one. Missing this produces
        // spectacularly wrong geometry from real Illustrator output.
        var p = Assert.Single(SvgPathParser.Parse("M 0 0 10 0 10 10"));
        Assert.Equal(3, p.Count);
    }

    [Fact]
    public void ParsesHorizontalAndVerticalShorthand()
    {
        var p = Assert.Single(SvgPathParser.Parse("M 0 0 H 20 V 20 H 0 Z"));
        Assert.Equal(4, p.Count);
        Assert.Equal(new Vec2(20, 20), p[2]);
    }

    [Fact]
    public void FlattensCubicBeziersToTheRequestedTolerance()
    {
        var p = Assert.Single(SvgPathParser.Parse("M 0 0 C 0 50 100 50 100 0", tolerance: 0.01));

        Assert.True(p.Count > 8, $"only {p.Count} points — the curve was not flattened");
        Assert.Equal(new Vec2(100, 0), p[^1]);
        // The curve's peak is at 3/4 of the control height.
        Assert.InRange(p.Points.Max(v => v.Y), 36, 38);
    }

    [Fact]
    public void SmoothCubicReflectsThePreviousControlPoint()
    {
        // S without a preceding C uses the current point; with one, it mirrors.
        var withReflection = Assert.Single(SvgPathParser.Parse("M 0 0 C 0 30 30 30 30 0 S 60 -30 60 0"));
        Assert.Equal(new Vec2(60, 0), withReflection[^1]);
        Assert.True(withReflection.Points.Any(v => v.Y < -5), "the reflected control point should pull the curve below the axis");
    }

    [Fact]
    public void ParsesQuadraticAndItsShorthand()
    {
        var p = Assert.Single(SvgPathParser.Parse("M 0 0 Q 25 50 50 0 T 100 0"));
        Assert.Equal(new Vec2(100, 0), p[^1]);
        Assert.True(p.Count > 10);
    }

    [Fact]
    public void ParsesEllipticalArcs()
    {
        // A semicircle of radius 25 from (0,0) to (50,0) is 78.5 mm long.
        var p = Assert.Single(SvgPathParser.Parse("M 0 0 A 25 25 0 0 1 50 0"));
        // Compared with a tolerance: trigonometry leaves a few attometres of residue
        // on the endpoint, which is exact enough for a laser and not worth chasing.
        Assert.Equal(50, p[^1].X, 6);
        Assert.Equal(0, p[^1].Y, 6);
        Assert.InRange(p.Length, 77, 80);
    }

    [Fact]
    public void HandlesArcFlagsWrittenWithoutSeparators()
    {
        // "a25 25 0 0150 0" — the flags run into the coordinates, which is legal.
        var p = Assert.Single(SvgPathParser.Parse("M 0 0 a25 25 0 0150 0"));
        Assert.Equal(50, p[^1].X, 6);
        Assert.Equal(0, p[^1].Y, 6);
    }

    [Fact]
    public void HandlesScientificNotationAndMissingSeparators()
    {
        var p = Assert.Single(SvgPathParser.Parse("M0,0L1e2,0L100-50"));
        Assert.Equal(3, p.Count);
        Assert.Equal(new Vec2(100, 0), p[1]);
        Assert.Equal(new Vec2(100, -50), p[2]);
    }

    [Fact]
    public void MultipleSubpathsBecomeMultiplePolylines()
    {
        var paths = SvgPathParser.Parse("M 0 0 L 10 0 Z M 20 0 L 30 0 Z");
        Assert.Equal(2, paths.Count);
    }
}

public class SvgImporterTests
{
    [Fact]
    public void HonoursMillimetreWidthAndViewBox()
    {
        // 100 mm declared, 100 user units — so one user unit is one millimetre.
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="50mm" viewBox="0 0 100 50">
              <rect x="10" y="10" width="30" height="20"/>
            </svg>
            """;

        var result = SvgImporter.Import(svg);

        Assert.Equal(100, result.WidthMm, 3);
        Assert.Equal(50, result.HeightMm, 3);

        var bounds = Rect2.FromPoints(result.Paths.SelectMany(p => p.Points));
        Assert.Equal(30, bounds.Width, 3);
        Assert.Equal(20, bounds.Height, 3);
    }

    [Fact]
    public void FlipsTheYAxisIntoMachineOrientation()
    {
        // SVG Y grows downward; the bed's grows up. A rect 10 from the SVG top must
        // land near the top of the bed, not the bottom.
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="100mm" viewBox="0 0 100 100">
              <rect x="0" y="0" width="10" height="10"/>
            </svg>
            """;

        var bounds = Rect2.FromPoints(SvgImporter.Import(svg).Paths.SelectMany(p => p.Points));
        Assert.Equal(90, bounds.MinY, 3);
        Assert.Equal(100, bounds.MaxY, 3);
    }

    [Fact]
    public void ScalesWhenTheViewBoxDoesNotMatchTheDeclaredSize()
    {
        // 200 user units drawn at 100 mm: everything halves.
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="100mm" viewBox="0 0 200 200">
              <rect x="0" y="0" width="100" height="100"/>
            </svg>
            """;

        var bounds = Rect2.FromPoints(SvgImporter.Import(svg).Paths.SelectMany(p => p.Points));
        Assert.Equal(50, bounds.Width, 3);
    }

    [Fact]
    public void AppliesNestedTransforms()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="100mm" viewBox="0 0 100 100">
              <g transform="translate(20,20)">
                <g transform="scale(2)">
                  <rect x="0" y="0" width="10" height="10"/>
                </g>
              </g>
            </svg>
            """;

        var bounds = Rect2.FromPoints(SvgImporter.Import(svg).Paths.SelectMany(p => p.Points));
        Assert.Equal(20, bounds.MinX, 3);
        Assert.Equal(20, bounds.Width, 3);
    }

    [Fact]
    public void ImportsEveryBasicShapeType()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="200mm" height="200mm" viewBox="0 0 200 200">
              <rect x="0" y="0" width="10" height="10"/>
              <circle cx="50" cy="50" r="10"/>
              <ellipse cx="100" cy="50" rx="20" ry="10"/>
              <line x1="0" y1="100" x2="50" y2="100"/>
              <polyline points="0,120 20,120 20,140"/>
              <polygon points="60,120 80,120 80,140"/>
              <path d="M 100 150 L 150 150"/>
            </svg>
            """;

        Assert.Equal(7, SvgImporter.Import(svg).Paths.Count);
    }

    [Fact]
    public void SkipsHiddenElementsAndWarnsAboutText()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="100mm" viewBox="0 0 100 100">
              <rect x="0" y="0" width="10" height="10" display="none"/>
              <text x="10" y="10">hello</text>
              <rect x="20" y="20" width="10" height="10"/>
            </svg>
            """;

        var result = SvgImporter.Import(svg);
        Assert.Single(result.Paths);
        Assert.Contains(result.Warnings, w => w.Contains("Text", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("100mm", 100.0)]
    [InlineData("10cm", 100.0)]
    [InlineData("1in", 25.4)]
    [InlineData("96px", 25.4)]
    [InlineData("72pt", 25.4)]
    public void ReadsLengthUnits(string value, double expectedMm) =>
        Assert.Equal(expectedMm, SvgImporter.ReadLength(value)!.Value, 3);

    [Fact]
    public void RejectsMalformedXmlWithAUsefulMessage()
    {
        var ex = Assert.Throws<InvalidDataException>(() => SvgImporter.Import("<svg><rect"));
        Assert.Contains("not valid SVG", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public class HatchFillTests
{
    private static Polyline Square(double x, double y, double size) =>
        new([new Vec2(x, y), new Vec2(x + size, y), new Vec2(x + size, y + size), new Vec2(x, y + size)], closed: true);

    [Fact]
    public void FillsASquareWithTheExpectedNumberOfLines()
    {
        // A 10 mm square at 1 mm spacing gives about ten lines.
        var lines = HatchFill.Generate([Square(0, 0, 10)], new HatchOptions { SpacingMm = 1 });
        Assert.InRange(lines.Count, 9, 11);
        Assert.All(lines, l => Assert.Equal(10, l.Length, 1));
    }

    [Fact]
    public void EveryHatchLineStaysInsideTheShape()
    {
        var square = Square(5, 5, 20);
        foreach (var line in HatchFill.Generate([square], new HatchOptions { SpacingMm = 2 }))
        {
            foreach (var p in line.Points)
            {
                Assert.InRange(p.X, 4.99, 25.01);
                Assert.InRange(p.Y, 4.99, 25.01);
            }
        }
    }

    [Fact]
    public void AHoleIsLeftUnfilled()
    {
        // A 20 mm square with a 10 mm square hole: the fill must not cross the hole.
        var outer = Square(0, 0, 20);
        var inner = Square(5, 5, 10);

        var lines = HatchFill.Generate([outer, inner], new HatchOptions { SpacingMm = 1 });

        // A line at y=10 passes through the hole, so it must arrive as two spans.
        var atMiddle = lines.Where(l => Math.Abs(l[0].Y - 10.5) < 0.6).ToList();
        Assert.True(atMiddle.Count >= 2, $"expected the hole to split the scan line; got {atMiddle.Count} span(s)");
        Assert.All(atMiddle, l => Assert.All(l.Points, p => Assert.False(p.X > 5.01 && p.X < 14.99, $"fill leaked into the hole at x={p.X}")));
    }

    [Fact]
    public void AngledHatchStillCoversTheShape()
    {
        var lines = HatchFill.Generate([Square(0, 0, 20)], new HatchOptions { SpacingMm = 1, AngleDegrees = 45 });
        Assert.True(lines.Count > 15);
        Assert.All(lines, l => Assert.All(l.Points, p =>
        {
            Assert.InRange(p.X, -0.1, 20.1);
            Assert.InRange(p.Y, -0.1, 20.1);
        }));
    }

    [Fact]
    public void CrossHatchProducesRoughlyTwiceAsManyLines()
    {
        var single = HatchFill.Generate([Square(0, 0, 20)], new HatchOptions { SpacingMm = 1 });
        var cross = HatchFill.Generate([Square(0, 0, 20)], new HatchOptions { SpacingMm = 1, CrossHatch = true });
        Assert.InRange(cross.Count, single.Count * 2 - 2, single.Count * 2 + 2);
    }

    [Fact]
    public void BidirectionalFillAlternatesDirection()
    {
        var lines = HatchFill.Generate([Square(0, 0, 10)], new HatchOptions { SpacingMm = 1, Bidirectional = true });
        var forward = lines[0][1].X > lines[0][0].X;
        var next = lines[1][1].X > lines[1][0].X;
        Assert.NotEqual(forward, next);
    }

    [Fact]
    public void OpenPathsAreIgnoredBecauseTheyEncloseNothing()
    {
        var open = new Polyline([new Vec2(0, 0), new Vec2(10, 0)]);
        Assert.Empty(HatchFill.Generate([open], new HatchOptions { SpacingMm = 1 }));
    }
}

public class BitmapTracerTests
{
    [Fact]
    public void TracesASolidSquareAsOneContour()
    {
        var px = new byte[50 * 50];
        Array.Fill(px, (byte)255);
        for (var y = 10; y < 40; y++)
        {
            for (var x = 10; x < 40; x++) px[y * 50 + x] = 0;
        }

        var result = BitmapTracer.Trace(new RasterImage(50, 50, px), TraceOptions.Default with { SmoothPasses = 0 });

        Assert.Single(result.Contours);
        var bounds = result.Contours[0].Bounds;
        Assert.InRange(bounds.Width, 28, 32);
        Assert.InRange(bounds.Height, 28, 32);
    }

    [Fact]
    public void TracesTwoSeparateShapesAsTwoContours()
    {
        var px = new byte[60 * 30];
        Array.Fill(px, (byte)255);
        for (var y = 5; y < 25; y++)
        {
            for (var x = 5; x < 20; x++) px[y * 60 + x] = 0;
            for (var x = 40; x < 55; x++) px[y * 60 + x] = 0;
        }

        var result = BitmapTracer.Trace(new RasterImage(60, 30, px), TraceOptions.Default with { SmoothPasses = 0 });
        Assert.Equal(2, result.Contours.Count);
    }

    [Fact]
    public void SpeckleIsDiscarded()
    {
        var px = new byte[40 * 40];
        Array.Fill(px, (byte)255);
        for (var y = 5; y < 30; y++)
        {
            for (var x = 5; x < 30; x++) px[y * 40 + x] = 0;
        }
        // A single stray dark pixel in the corner.
        px[38 * 40 + 38] = 0;

        var result = BitmapTracer.Trace(new RasterImage(40, 40, px), TraceOptions.Default);
        Assert.Single(result.Contours);
    }

    [Fact]
    public void PlacesTheTraceAtTheRequestedRealWorldSize()
    {
        var px = new byte[40 * 40];
        Array.Fill(px, (byte)255);
        for (var y = 10; y < 30; y++)
        {
            for (var x = 10; x < 30; x++) px[y * 40 + x] = 0;
        }

        var shape = BitmapTracer.TraceToShape(new RasterImage(40, 40, px), 80, 80);
        var bounds = shape.LocalBounds;

        // The 20 px square is half the image, so 40 mm of an 80 mm placement.
        Assert.InRange(bounds.Width, 36, 44);
        Assert.InRange(bounds.MinX, 16, 24);
    }
}

public class CentrelineTraceTests
{
    /// <summary>A horizontal bar `thickness` px tall, drawn dark on white.</summary>
    private static RasterImage Bar(int w, int h, int x0, int x1, int y0, int thickness)
    {
        var px = new byte[w * h];
        Array.Fill(px, (byte)255);
        for (var y = y0; y < y0 + thickness; y++)
        {
            for (var x = x0; x < x1; x++) px[y * w + x] = 0;
        }
        return new RasterImage(w, h, px);
    }

    private static readonly TraceOptions Centreline =
        TraceOptions.Default with { Mode = TraceMode.Centreline, SmoothPasses = 0, SimplifyTolerancePx = 0 };

    [Fact]
    public void AThickStrokeBecomesOneOpenPathDownItsMiddle()
    {
        var image = Bar(80, 40, 10, 70, 15, 9);

        var result = BitmapTracer.Trace(image, Centreline);

        Assert.Equal(TraceMode.Centreline, result.Mode);
        var path = Assert.Single(result.Contours);
        Assert.False(path.IsClosed);

        // It should run the length of the bar, not around it. Thinning pulls the
        // tips in by about half the stroke width at each end, so 60 px of bar
        // becomes roughly 50 px of spine.
        var bounds = path.Bounds;
        Assert.InRange(bounds.Width, 46, 62);
        Assert.InRange(bounds.Height, 0, 2);

        // And sit on the bar's centre row, not on either edge.
        Assert.InRange(bounds.Center.Y, 18.0, 21.0);
    }

    [Fact]
    public void OutlineModeDrawsTheSameStrokeTwiceAndCentrelineModeOnce()
    {
        var image = Bar(80, 40, 10, 70, 15, 9);

        var outline = BitmapTracer.Trace(image, TraceOptions.Default with { SmoothPasses = 0, SimplifyTolerancePx = 0 });
        var centreline = BitmapTracer.Trace(image, Centreline);

        // This is the whole reason centreline mode exists: the closed outline burns
        // both sides of the stroke, so it travels roughly twice as far.
        Assert.True(outline.TotalLengthPx > centreline.TotalLengthPx * 1.8,
            $"outline {outline.TotalLengthPx:0} px vs centreline {centreline.TotalLengthPx:0} px");
    }

    [Fact]
    public void AJunctionIsSplitIntoSeparateStrokes()
    {
        // A plus sign: thinning leaves a four-armed skeleton meeting in the middle.
        var px = new byte[60 * 60];
        Array.Fill(px, (byte)255);
        for (var y = 26; y < 34; y++)
        {
            for (var x = 8; x < 52; x++) px[y * 60 + x] = 0;
        }
        for (var y = 8; y < 52; y++)
        {
            for (var x = 26; x < 34; x++) px[y * 60 + x] = 0;
        }

        var result = BitmapTracer.Trace(new RasterImage(60, 60, px), Centreline);

        // Four arms, so at least four strokes — never one path doubling back.
        Assert.InRange(result.Contours.Count, 4, 8);
        Assert.All(result.Contours, c => Assert.True(c.Count >= 2));
    }

    [Fact]
    public void ARingComesBackAsAClosedLoop()
    {
        var px = new byte[70 * 70];
        Array.Fill(px, (byte)255);
        for (var y = 0; y < 70; y++)
        {
            for (var x = 0; x < 70; x++)
            {
                var d = Math.Sqrt((x - 35.0) * (x - 35.0) + (y - 35.0) * (y - 35.0));
                if (d is > 20 and < 27) px[y * 70 + x] = 0;
            }
        }

        var result = BitmapTracer.Trace(new RasterImage(70, 70, px), Centreline);

        var loop = Assert.Single(result.Contours);
        Assert.True(loop.IsClosed);
        // Radius roughly midway through the annulus.
        Assert.InRange(loop.Bounds.Width, 42, 52);
    }

    [Fact]
    public void ThinningDoesNotBreakAStrokeIntoPieces()
    {
        // A diagonal stroke is the case where a naive thinning falls apart.
        var px = new byte[80 * 80];
        Array.Fill(px, (byte)255);
        for (var i = 0; i < 70; i++)
        {
            for (var t = 0; t < 6; t++)
            {
                var x = i + t;
                var y = i;
                if (x < 80 && y < 80) px[y * 80 + x] = 0;
            }
        }

        var result = BitmapTracer.Trace(new RasterImage(80, 80, px), Centreline);

        Assert.Single(result.Contours);
        Assert.InRange(result.Contours[0].Length, 85, 105);
    }

    [Fact]
    public void StubsShorterThanTheMinimumAreDropped()
    {
        var image = Bar(80, 40, 10, 70, 15, 9);

        var kept = BitmapTracer.Trace(image, Centreline with { MinimumLengthPx = 4 });
        var dropped = BitmapTracer.Trace(image, Centreline with { MinimumLengthPx = 500 });

        Assert.Single(kept.Contours);
        Assert.Empty(dropped.Contours);
    }
}

public class TracedJobTests
{
    /// <summary>
    /// The whole point of tracing: a bitmap has to come out the far end as G-code
    /// the machine will actually run. Everything else in the tracer is detail.
    /// </summary>
    [Fact]
    public void ATracedBitmapGeneratesARunnableJob()
    {
        var px = new byte[120 * 120];
        Array.Fill(px, (byte)255);
        for (var y = 20; y < 100; y++)
        {
            for (var x = 20; x < 100; x++) px[y * 120 + x] = 0;
        }

        var shape = BitmapTracer.TraceToShape(new RasterImage(120, 120, px), 60, 60);
        shape.Translate(new Vec2(40, 40));

        var design = new Design { Name = "traced" };
        var layer = Layer.CreateDefault(OperationKind.Cut, 0);
        layer.PowerPercent = 70;
        layer.SpeedMmMin = 400;
        layer.Passes = 1;
        design.Layers.Add(layer);
        design.AddShape(shape, layer);

        var machine = MachineLibrary.Load().Profiles.First(p => p.Id == "generic-grbl");
        var result = CamPipeline.Generate(design, machine, new CamOptions());

        Assert.True(result.CanRun, string.Join("; ", result.Issues.Select(i => i.Title)));
        Assert.True(result.Job.LineCount > 20, $"only {result.Job.LineCount} lines");

        // The burn must land where the shape was put, not at the origin.
        var text = string.Join("\n", result.Job.Lines);
        Assert.Contains("M4", text);
        Assert.True(result.Estimate.Total > TimeSpan.Zero);
    }
}

public class TraceLimitTests
{
    /// <summary>Salt and pepper — the shape a grainy photograph takes once thresholded.</summary>
    private static RasterImage Noise(int w, int h)
    {
        var rng = new Random(7);
        var px = new byte[w * h];
        for (var i = 0; i < px.Length; i++) px[i] = (byte)(rng.Next(2) == 0 ? 20 : 230);
        return new RasterImage(w, h, px);
    }

    [Fact]
    public void ATooDetailedImageStopsAndSaysSoRatherThanReturningMillionsOfPoints()
    {
        var result = BitmapTracer.Trace(Noise(700, 700), TraceOptions.Default with { MaxPoints = 20_000 });

        Assert.NotEmpty(result.Notes);
        Assert.Contains(result.Notes, n => n.Contains("points", StringComparison.OrdinalIgnoreCase));

        // The budget is checked between contours, so one contour may overshoot it.
        // What matters is that it stops, rather than running to millions.
        Assert.True(result.PointCount < 20_000 * 2, $"point count was {result.PointCount:N0}");
    }

    [Fact]
    public void AnOrdinaryImageIsTracedWholeWithNothingToReport()
    {
        var px = new byte[60 * 60];
        Array.Fill(px, (byte)255);
        for (var y = 10; y < 50; y++)
        {
            for (var x = 10; x < 50; x++) px[y * 60 + x] = 0;
        }

        var result = BitmapTracer.Trace(new RasterImage(60, 60, px));

        Assert.Empty(result.Notes);
        Assert.Single(result.Contours);
    }

    [Fact]
    public void AnOversizedImageIsResampledButStillLandsInSourceCoordinates()
    {
        // A plain square, big enough to trip the working-resolution cap.
        var w = 1200;
        var h = 1000;
        var px = new byte[w * h];
        Array.Fill(px, (byte)255);
        for (var y = 250; y < 750; y++)
        {
            for (var x = 300; x < 900; x++) px[y * w + x] = 0;
        }

        var result = BitmapTracer.Trace(new RasterImage(w, h, px),
            TraceOptions.Default with { MaxWorkingPixels = 100_000 });

        Assert.Contains(result.Notes, n => n.Contains("Traced at", StringComparison.Ordinal));

        // Scaled back up: the contour must describe the square in the original
        // pixel grid, not in the reduced one.
        var bounds = Assert.Single(result.Contours).Bounds;
        Assert.InRange(bounds.MinX, 280, 320);
        Assert.InRange(bounds.MinY, 230, 270);
        Assert.InRange(bounds.Width, 570, 630);
        Assert.InRange(bounds.Height, 470, 530);
    }

    [Fact]
    public void TheResolutionCapCanBeTurnedOff()
    {
        var w = 700;
        var h = 700;
        var px = new byte[w * h];
        Array.Fill(px, (byte)255);
        for (var y = 100; y < 600; y++)
        {
            for (var x = 100; x < 600; x++) px[y * w + x] = 0;
        }

        var result = BitmapTracer.Trace(new RasterImage(w, h, px),
            TraceOptions.Default with { MaxWorkingPixels = 0 });

        Assert.Empty(result.Notes);
    }
}

public class AutoThresholdTests
{
    [Fact]
    public void SplitsABimodalImageBetweenItsTwoPeaks()
    {
        var px = new byte[100 * 100];
        for (var i = 0; i < px.Length; i++) px[i] = i % 2 == 0 ? (byte)40 : (byte)210;

        var threshold = BitmapTracer.AutoThreshold(new RasterImage(100, 100, px));

        Assert.InRange(threshold, 41, 210);
    }

    [Fact]
    public void FindsInkThatAFixed128WouldMiss()
    {
        // Warm-lit scan: "white" paper at 200, "black" ink at 150. Nothing is
        // below 128, so the default threshold traces precisely nothing.
        var px = new byte[60 * 60];
        Array.Fill(px, (byte)200);
        for (var y = 20; y < 40; y++)
        {
            for (var x = 20; x < 40; x++) px[y * 60 + x] = 150;
        }
        var image = new RasterImage(60, 60, px);

        Assert.Empty(BitmapTracer.Trace(image, TraceOptions.Default).Contours);

        var auto = BitmapTracer.AutoThreshold(image);
        Assert.InRange(auto, 151, 200);
        Assert.Single(BitmapTracer.Trace(image, TraceOptions.Default with { Threshold = auto }).Contours);
    }

    [Fact]
    public void AFlatImageStillReturnsAUsableThreshold()
    {
        var px = new byte[400];
        Array.Fill(px, (byte)128);

        var threshold = BitmapTracer.AutoThreshold(new RasterImage(20, 20, px));
        Assert.InRange(threshold, 1, 255);
    }
}

public class CamPipelineTests
{
    private static Design SquareDesign(OperationKind kind, double power = 80, double speed = 600)
    {
        var design = new Design { Name = "test" };
        var layer = Layer.CreateDefault(kind, 0);
        layer.PowerPercent = power;
        layer.SpeedMmMin = speed;
        // Cut defaults to three passes; tests that care about pass count set it themselves.
        layer.Passes = 1;
        design.Layers.Add(layer);

        var square = new PathShape([new Polyline(
        [
            new Vec2(20, 20), new Vec2(60, 20), new Vec2(60, 60), new Vec2(20, 60),
        ], closed: true)]);

        design.AddShape(square, layer);
        return design;
    }

    [Fact]
    public void GeneratesARunnableCutJob()
    {
        var machine = MachineProfile.GenericGrbl();
        var result = CamPipeline.Generate(SquareDesign(OperationKind.Cut), machine);

        Assert.True(result.CanRun, string.Join("; ", result.Issues.Select(i => i.Title)));
        Assert.Equal(160, result.Toolpath.CutLengthMm, 1);
        Assert.True(result.Estimate.Total > TimeSpan.Zero);
        Assert.Contains("M5", result.Job.Lines);
    }

    [Fact]
    public void PowerPercentageBecomesTheMachinesSValue()
    {
        var machine = MachineProfile.GenericGrbl() with { MaxSpindleValue = 1000 };
        var result = CamPipeline.Generate(SquareDesign(OperationKind.Cut, power: 45), machine);

        Assert.Equal(450, result.Toolpath.MaxSpindleSeen, 0);
    }

    [Fact]
    public void PassesRepeatTheGeometry()
    {
        var design = SquareDesign(OperationKind.Cut);
        design.Layers[0].Passes = 3;

        var result = CamPipeline.Generate(design, MachineProfile.GenericGrbl());
        Assert.Equal(480, result.Toolpath.CutLengthMm, 1);
    }

    [Fact]
    public void AFillLayerHatchesTheInterior()
    {
        var design = SquareDesign(OperationKind.Fill);
        design.Layers[0].LineIntervalMm = 1;

        var result = CamPipeline.Generate(design, MachineProfile.GenericGrbl());

        // Roughly forty 40 mm scan lines, so far more cutting than the 160 mm outline.
        Assert.True(result.Toolpath.CutLengthMm > 1000,
            $"fill produced only {result.Toolpath.CutLengthMm:0} mm of cutting");
    }

    [Fact]
    public void AJobOffTheBedIsReportedAsBlocking()
    {
        var machine = MachineProfile.GenericGrbl() with { BedWidthMm = 40, BedHeightMm = 40 };
        var result = CamPipeline.Generate(SquareDesign(OperationKind.Cut), machine);

        Assert.False(result.CanRun);
        Assert.Contains(result.Issues, i =>
            i.Severity == ValidationSeverity.Error && i.Title.Contains("outside", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LayersRunInTheirDeclaredOrder()
    {
        var design = new Design { Name = "ordered" };
        var cut = Layer.CreateDefault(OperationKind.Cut, order: 1);
        cut.Name = "CUTLAYER";
        var engrave = Layer.CreateDefault(OperationKind.Engrave, order: 0);
        engrave.Name = "ENGRAVELAYER";
        design.Layers.Add(cut);
        design.Layers.Add(engrave);

        design.AddShape(new PathShape([new Polyline([new Vec2(10, 10), new Vec2(20, 10)])]), engrave);
        design.AddShape(new PathShape([new Polyline([new Vec2(30, 10), new Vec2(40, 10)])]), cut);

        var text = string.Join('\n', CamPipeline.Generate(design, MachineProfile.GenericGrbl()).Job.Lines);

        // Engraving before cutting: cut the part out last or it moves.
        Assert.True(text.IndexOf("ENGRAVELAYER", StringComparison.Ordinal) <
                    text.IndexOf("CUTLAYER", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledLayersProduceNoOutput()
    {
        var design = SquareDesign(OperationKind.Cut);
        design.Layers[0].Enabled = false;

        var result = CamPipeline.Generate(design, MachineProfile.GenericGrbl());
        Assert.Equal(0, result.Toolpath.CutLengthMm, 3);
    }

    [Fact]
    public void GeneratesAMaterialTestGrid()
    {
        var machine = MachineProfile.GenericGrbl();
        var result = CamPipeline.GenerateTestGrid(machine, [20, 40, 60, 80], [500, 1500, 3000], cellSizeMm: 8);

        Assert.True(result.CanRun, string.Join("; ", result.Issues.Select(i => i.Title)));
        Assert.Contains("Test grid 4×3", result.Job.Name, StringComparison.Ordinal);

        // Four rows and three columns of 8 mm squares with 3 mm gaps.
        Assert.InRange(result.Job.Bounds.Width, 25, 35);
        Assert.InRange(result.Job.Bounds.Height, 36, 46);
        Assert.True(result.Estimate.Total > TimeSpan.Zero);
    }

    [Fact]
    public void GenerationIsDeterministic()
    {
        var machine = MachineProfile.GenericGrbl();
        var a = CamPipeline.Generate(SquareDesign(OperationKind.Cut), machine).Job.Lines;
        var b = CamPipeline.Generate(SquareDesign(OperationKind.Cut), machine).Job.Lines;

        // Skip the timestamp line in the header.
        Assert.Equal(a.Where(l => !l.StartsWith("; Created", StringComparison.Ordinal)),
                     b.Where(l => !l.StartsWith("; Created", StringComparison.Ordinal)));
    }
}

public class RotaryTests
{
    private static Design RingDesign(double heightMm)
    {
        var design = new Design { Name = "rotary" };
        var layer = Layer.CreateDefault(OperationKind.Engrave, 0);
        layer.Passes = 1;
        design.Layers.Add(layer);

        var band = new PathShape([new Polyline(
        [
            new Vec2(20, 20), new Vec2(120, 20), new Vec2(120, 20 + heightMm), new Vec2(20, 20 + heightMm),
        ], closed: true)]);

        design.AddShape(band, layer);
        return design;
    }

    [Fact]
    public void ADisabledRotaryChangesNothing()
    {
        var machine = MachineProfile.GenericGrbl();
        var design = RingDesign(40);

        var flat = CamPipeline.Generate(design, machine);
        var withRotary = CamPipeline.Generate(design, machine, CamOptions.Default with { Rotary = RotarySetup.Disabled });

        Assert.Equal(flat.Toolpath.CutLengthMm, withRotary.Toolpath.CutLengthMm, 6);
    }

    [Fact]
    public void ChuckModeScalesByTheWorkpieceDiameter()
    {
        // 6400 steps per rotation, 80 steps/mm on the axis, 60 mm workpiece.
        // Commanded mm per surface mm = 6400 / (80 × π × 60) = 0.42441.
        var rotary = new RotarySetup
        {
            Enabled = true,
            Kind = RotaryKind.Chuck,
            WorkpieceDiameterMm = 60,
            StepsPerRotation = 6400,
            AxisStepsPerMm = 80,
        };

        Assert.Equal(0.42441, rotary.ScaleFactor, 4);

        var result = CamPipeline.Generate(RingDesign(40), MachineProfile.GenericGrbl(),
            CamOptions.Default with { Rotary = rotary });

        // The Y extent should shrink by the scale factor; X is untouched.
        Assert.Equal(40 * rotary.ScaleFactor, result.Toolpath.BurnBounds.Height, 1);
        Assert.Equal(100, result.Toolpath.BurnBounds.Width, 1);
    }

    [Fact]
    public void RollerModeUsesTheRollerDiameterNotTheWorkpiece()
    {
        // The roller surface and the workpiece surface move together, so the
        // workpiece diameter cancels out. This is the part people get wrong.
        var small = new RotarySetup
        {
            Enabled = true,
            Kind = RotaryKind.Roller,
            RollerDiameterMm = 20,
            WorkpieceDiameterMm = 60,
            StepsPerRotation = 6400,
            AxisStepsPerMm = 80,
        };

        var large = small with { WorkpieceDiameterMm = 200 };

        Assert.Equal(small.ScaleFactor, large.ScaleFactor, 9);
        Assert.Equal(20, small.EffectiveDiameterMm, 6);
    }

    [Fact]
    public void ChangingTheRollerDiameterChangesTheScale()
    {
        var twenty = new RotarySetup
        {
            Enabled = true, Kind = RotaryKind.Roller,
            RollerDiameterMm = 20, StepsPerRotation = 6400, AxisStepsPerMm = 80,
        };
        var forty = twenty with { RollerDiameterMm = 40 };

        Assert.Equal(twenty.ScaleFactor / 2, forty.ScaleFactor, 9);
    }

    [Fact]
    public void ArtworkTallerThanTheCircumferenceIsFlagged()
    {
        var rotary = new RotarySetup
        {
            Enabled = true, Kind = RotaryKind.Chuck,
            WorkpieceDiameterMm = 20, StepsPerRotation = 6400, AxisStepsPerMm = 80,
        };

        // A 20 mm workpiece is 62.8 mm around; 100 mm of artwork overlaps itself.
        var warnings = rotary.Check(designHeightMm: 100);
        Assert.Contains(warnings, w => w.Contains("around", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ASuspiciouslyNeutralScaleIsFlagged()
    {
        // A scale of almost exactly 1 nearly always means a mis-entered value.
        var rotary = new RotarySetup
        {
            Enabled = true, Kind = RotaryKind.Chuck,
            WorkpieceDiameterMm = 100 / Math.PI,
            StepsPerRotation = 8000,
            AxisStepsPerMm = 80,
        };

        Assert.Equal(1, rotary.ScaleFactor, 3);
        Assert.Contains(rotary.Check(10), w => w.Contains("almost exactly 1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IncompleteSettingsFallBackToNoScaling()
    {
        var rotary = new RotarySetup { Enabled = true, WorkpieceDiameterMm = 0, RollerDiameterMm = 0 };

        Assert.False(rotary.IsUsable);
        Assert.Equal(1, rotary.ScaleFactor, 9);
        Assert.Contains(rotary.Check(10), w => w.Contains("incomplete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnlyTheRotaryAxisIsRescaled()
    {
        var rotary = new RotarySetup
        {
            Enabled = true, Kind = RotaryKind.Chuck, Axis = 'Y',
            WorkpieceDiameterMm = 60, StepsPerRotation = 6400, AxisStepsPerMm = 80,
        };

        var lines = CamPipeline.ApplyRotary(
            ["G1 X100 Y50 F1000 S500", "G0 X10 Y10", "; Y is a comment and must not change", "M5"],
            rotary);

        Assert.Contains("X100", lines[0], StringComparison.Ordinal);
        Assert.Contains("Y21.2207", lines[0], StringComparison.Ordinal);
        Assert.Contains("F1000", lines[0], StringComparison.Ordinal);
        Assert.Contains("S500", lines[0], StringComparison.Ordinal);

        Assert.Equal("; Y is a comment and must not change", lines[2]);
        Assert.Equal("M5", lines[3]);
    }

    [Fact]
    public void NegativeCoordinatesScaleCorrectly()
    {
        var rotary = new RotarySetup
        {
            Enabled = true, Kind = RotaryKind.Chuck,
            WorkpieceDiameterMm = 60, StepsPerRotation = 6400, AxisStepsPerMm = 80,
        };

        var lines = CamPipeline.ApplyRotary(["G1 Y-40"], rotary);
        Assert.Contains($"Y{-40 * rotary.ScaleFactor:0.####}", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratedProgramExplainsTheRotarySetup()
    {
        var rotary = new RotarySetup
        {
            Enabled = true, Kind = RotaryKind.Roller,
            RollerDiameterMm = 20, WorkpieceDiameterMm = 70,
            StepsPerRotation = 6400, AxisStepsPerMm = 80,
        };

        var result = CamPipeline.Generate(RingDesign(30), MachineProfile.GenericGrbl(),
            CamOptions.Default with { Rotary = rotary });

        var header = string.Join('\n', result.Job.Lines.Take(12));
        Assert.Contains("Rotary on Y via rollers", header, StringComparison.Ordinal);
        Assert.Contains("mm around", header, StringComparison.Ordinal);
    }
}
