using Emberline.Cam.Text;
using Emberline.Core.Documents;
using Emberline.Core.Geometry;
using Xunit;

namespace Emberline.Cam.Tests;

public class TextOutlinerTests
{
    [Fact]
    public void EmptyTextProducesNothing()
    {
        var result = TextOutliner.Create("");
        Assert.Empty(result.Outlines);
        Assert.Equal(0, result.WidthMm);
    }

    [Fact]
    public void ASingleLetterProducesClosedContours()
    {
        var result = TextOutliner.Create("A", TextLayoutOptions.Default with { FontSizeMm = 20 });

        Assert.NotEmpty(result.Outlines);
        Assert.All(result.Outlines, p => Assert.True(p.IsClosed, "glyph contours must be closed or they cut as open lines"));
        Assert.All(result.Outlines, p => Assert.True(p.Count >= 3));
    }

    [Fact]
    public void GlyphHeightTracksTheRequestedSize()
    {
        // A capital letter's height is a predictable fraction of the em — around
        // 0.7 for most text faces. The test is loose because it must hold for
        // whatever font the machine actually has.
        var small = TextOutliner.Create("H", TextLayoutOptions.Default with { FontSizeMm = 10 });
        var large = TextOutliner.Create("H", TextLayoutOptions.Default with { FontSizeMm = 40 });

        Assert.InRange(small.HeightMm, 5, 11);
        Assert.InRange(large.HeightMm / small.HeightMm, 3.8, 4.2);
    }

    [Fact]
    public void CountersSurviveAsSeparateContours()
    {
        // The letter O is two contours: the outside and the hole. Merging them
        // fills the middle in, which is the classic text-to-path failure.
        var result = TextOutliner.Create("O", TextLayoutOptions.Default with { FontSizeMm = 30 });
        Assert.True(result.Outlines.Count >= 2, $"expected an outer contour and a counter, got {result.Outlines.Count}");

        var areas = result.Outlines.Select(p => Math.Abs(p.SignedArea)).OrderByDescending(a => a).ToList();
        Assert.True(areas[1] > areas[0] * 0.1, "the counter should be a substantial fraction of the glyph");
    }

    [Fact]
    public void MoreCharactersMakeWiderText()
    {
        var one = TextOutliner.Create("I", TextLayoutOptions.Default with { FontSizeMm = 12 });
        var many = TextOutliner.Create("IIIII", TextLayoutOptions.Default with { FontSizeMm = 12 });
        Assert.True(many.WidthMm > one.WidthMm * 3);
    }

    [Fact]
    public void LetterSpacingWidensTheResult()
    {
        var tight = TextOutliner.Create("OPEN", TextLayoutOptions.Default with { FontSizeMm = 12 });
        var loose = TextOutliner.Create("OPEN", TextLayoutOptions.Default with { FontSizeMm = 12, LetterSpacingMm = 3 });

        // Three gaps of three millimetres.
        Assert.InRange(loose.WidthMm - tight.WidthMm, 7, 11);
    }

    [Fact]
    public void MultipleLinesStackDownwards()
    {
        var one = TextOutliner.Create("X", TextLayoutOptions.Default with { FontSizeMm = 10 });
        var three = TextOutliner.Create("X\nX\nX", TextLayoutOptions.Default with { FontSizeMm = 10, LineHeightFactor = 1.2 });

        // Two extra line pitches of 12 mm on top of the glyph height.
        Assert.InRange(three.HeightMm - one.HeightMm, 22, 26);
    }

    [Fact]
    public void CentreAlignmentCentresTheShorterLine()
    {
        var result = TextOutliner.Create("WWWWW\nI", TextLayoutOptions.Default with
        {
            FontSizeMm = 12,
            Alignment = TextAlignment.Center,
        });

        // The narrow line's contours must sit away from the left edge.
        var bounds = Rect2.FromPoints(result.Outlines.SelectMany(p => p.Points));
        var narrow = result.Outlines
            .Where(p => p.Bounds.MaxY < bounds.MinY + bounds.Height * 0.5)
            .ToList();

        Assert.NotEmpty(narrow);
        var narrowBounds = Rect2.FromPoints(narrow.SelectMany(p => p.Points));
        Assert.True(narrowBounds.MinX > bounds.Width * 0.15,
            $"the short line starts at {narrowBounds.MinX:0.#} of {bounds.Width:0.#} mm — not centred");
    }

    [Fact]
    public void OutlinesAreOriginedAtTheBottomLeft()
    {
        var result = TextOutliner.Create("Ag", TextLayoutOptions.Default with { FontSizeMm = 15 });
        var bounds = Rect2.FromPoints(result.Outlines.SelectMany(p => p.Points));

        Assert.Equal(0, bounds.MinX, 6);
        Assert.Equal(0, bounds.MinY, 6);
    }

    [Fact]
    public void TextIsTheRightWayUpForTheBed()
    {
        // Font space grows downward, the bed grows upward. If the flip is missed,
        // text engraves mirrored about its baseline. A lower-case 'b' has its bowl
        // below its ascender, so the heavier half must be at the bottom.
        var result = TextOutliner.Create("b", TextLayoutOptions.Default with { FontSizeMm = 30 });
        var bounds = Rect2.FromPoints(result.Outlines.SelectMany(p => p.Points));
        var midpoint = bounds.MinY + bounds.Height / 2;

        var points = result.Outlines.SelectMany(p => p.Points).ToList();
        var below = points.Count(p => p.Y < midpoint);
        var above = points.Count(p => p.Y >= midpoint);

        Assert.True(below > above, $"the bowl should be the lower half — {below} below, {above} above");
    }

    [Fact]
    public void ApplyFillsATextShape()
    {
        var shape = new TextShape { Text = "Finton", FontSizeMm = 18 };
        Assert.True(shape.OutlinesStale);

        var result = TextOutliner.Apply(shape);

        Assert.False(shape.OutlinesStale);
        Assert.NotEmpty(shape.Outlines);
        Assert.True(shape.LocalBounds.Width > 20);
        Assert.Equal(shape.Outlines.Count, result.Outlines.Count);
    }

    [Fact]
    public void ConvertingToPathsKeepsTheGeometry()
    {
        var shape = new TextShape { Text = "Cut", FontSizeMm = 20 };
        TextOutliner.Apply(shape);

        var paths = shape.ToPathShape();
        Assert.Equal(shape.Outlines.Count, paths.Paths.Count);
        Assert.Equal(shape.LocalBounds.Width, paths.LocalBounds.Width, 6);
    }

    [Fact]
    public void AtLeastOneFontFamilyIsAvailable() =>
        Assert.NotEmpty(TextOutliner.AvailableFamilies());

    [Fact]
    public void OutputIsDeterministic()
    {
        var a = TextOutliner.Create("Repeatable", TextLayoutOptions.Default with { FontSizeMm = 14 });
        var b = TextOutliner.Create("Repeatable", TextLayoutOptions.Default with { FontSizeMm = 14 });

        Assert.Equal(a.Outlines.Count, b.Outlines.Count);
        Assert.Equal(a.WidthMm, b.WidthMm, 9);
    }
}
