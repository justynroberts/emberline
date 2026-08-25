using Emberline.Core.Documents;
using Emberline.Core.Geometry;
using Xunit;

namespace Emberline.Core.Tests;

/// <summary>
/// The material on the bed. A bed is 400 mm square; the thing being engraved is
/// often a 100 mm tile somewhere in the middle of it.
/// </summary>
public class WorkpieceTests
{
    [Fact]
    public void NoWorkpieceMeansTheWholeBedIsFairGame()
    {
        Assert.False(Workpiece.None.IsSet);
        Assert.Empty(Workpiece.None.Outline());
        Assert.True(Workpiece.None.Contains(new Rect2(0, 0, 1000, 1000)));
    }

    [Fact]
    public void ASquareBlankSitsWhereItIsPut()
    {
        var w = new Workpiece { Shape = WorkpieceShape.Rectangle, WidthMm = 100, HeightMm = 100, XMm = 30, YMm = 40 };

        Assert.True(w.IsSet);
        Assert.Equal("100 mm square", w.Summary);

        var bounds = w.Outline()[0].Bounds;
        Assert.Equal(30, bounds.MinX, 3);
        Assert.Equal(40, bounds.MinY, 3);
        Assert.Equal(100, bounds.Width, 3);
        Assert.Equal(100, bounds.Height, 3);
    }

    [Fact]
    public void ACircleIsDescribedByItsDiameterNotItsRadius()
    {
        // Ellipse takes radii and is centred on the origin, so this is the easy
        // one to get twice as large and in the wrong place.
        var w = new Workpiece { Shape = WorkpieceShape.Circle, WidthMm = 80, HeightMm = 80, XMm = 10, YMm = 20 };

        var bounds = w.Outline(0.01)[0].Bounds;

        Assert.Equal(80, bounds.Width, 1);
        Assert.Equal(80, bounds.Height, 1);
        Assert.Equal(10, bounds.MinX, 1);
        Assert.Equal(20, bounds.MinY, 1);
        Assert.Equal("80 mm circle", w.Summary);
    }

    [Fact]
    public void CentringPutsItInTheMiddleOfTheBed()
    {
        var w = new Workpiece { Shape = WorkpieceShape.Rectangle, WidthMm = 100, HeightMm = 60 }
            .CentredOn(400, 400);

        Assert.Equal(150, w.XMm, 3);
        Assert.Equal(170, w.YMm, 3);
    }

    [Fact]
    public void ABlankLargerThanTheBedIsPinnedToTheCornerRatherThanPushedOffIt()
    {
        var w = new Workpiece { Shape = WorkpieceShape.Rectangle, WidthMm = 900, HeightMm = 900 }
            .CentredOn(400, 400);

        Assert.Equal(0, w.XMm, 3);
        Assert.Equal(0, w.YMm, 3);
    }

    [Fact]
    public void ArtworkInsideTheBlankFits()
    {
        var w = new Workpiece { Shape = WorkpieceShape.Rectangle, WidthMm = 100, HeightMm = 100, XMm = 50, YMm = 50 };

        Assert.True(w.Contains(new Rect2(60, 60, 80, 80)));
        Assert.Equal((0, 0), w.Overhang(new Rect2(60, 60, 80, 80)));
    }

    [Fact]
    public void ArtworkHangingOffTheEdgeIsMeasured()
    {
        var w = new Workpiece { Shape = WorkpieceShape.Rectangle, WidthMm = 100, HeightMm = 100, XMm = 50, YMm = 50 };

        var (x, y) = w.Overhang(new Rect2(40, 55, 70, 85));

        Assert.Equal(10, x, 3);      // ten millimetres off the left edge
        Assert.Equal(0, y, 3);
        Assert.False(w.Contains(new Rect2(40, 55, 70, 85)));
    }

    [Fact]
    public void OverhangIsMeasuredPastBothEdges()
    {
        var w = new Workpiece { Shape = WorkpieceShape.Rectangle, WidthMm = 100, HeightMm = 100 };

        var (x, y) = w.Overhang(new Rect2(-5, 10, 125, 60));

        Assert.Equal(25, x, 3);      // the larger of the two overruns
        Assert.Equal(0, y, 3);
    }
}
