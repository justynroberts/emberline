using OpenBurn.Cam;
using OpenBurn.Cam.Vector;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;
using OpenBurn.Core.Machines;
using Xunit;

namespace OpenBurn.Cam.Tests;

/// <summary>
/// Combining overlapping outlines.
///
/// Cutting closed paths one at a time is wrong the moment any of them overlap:
/// the beam runs through the middle of the finished piece. What is wanted is the
/// boundary of the union, with the shared interior left alone — and with holes
/// still holes.
/// </summary>
public class PathBooleanTests
{
    private static Polyline Circle(double cx, double cy, double r, int steps = 96)
    {
        var p = new Polyline { IsClosed = true };
        for (var i = 0; i < steps; i++)
        {
            var a = 2 * Math.PI * i / steps;
            p.Add(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }
        return p;
    }

    private static Polyline Square(double x, double y, double size) => new(
    [
        new Vec2(x, y), new Vec2(x + size, y), new Vec2(x + size, y + size), new Vec2(x, y + size),
    ], closed: true);

    [Fact]
    public void TwoOverlappingShapesBecomeOneOutline()
    {
        // The whole point: cut separately, the beam crosses the middle.
        var merged = PathBooleans.Union([Circle(0, 0, 10), Circle(12, 0, 10)]);

        Assert.Single(merged);

        var bounds = merged[0].Bounds;
        Assert.Equal(-10, bounds.MinX, 0);
        Assert.Equal(22, bounds.MaxX, 0);
    }

    [Fact]
    public void TheSharedInteriorIsNotCut()
    {
        // Nothing in the result may pass through the overlap.
        var merged = PathBooleans.Union([Square(0, 0, 20), Square(10, 0, 20)]);

        var overlapCentre = new Vec2(15, 10);
        foreach (var path in merged)
        {
            for (var i = 0; i < path.Count; i++)
            {
                Assert.True((path[i] - overlapCentre).Length > 4,
                    $"a cut passes through the shared interior at {path[i]}");
            }
        }
    }

    [Fact]
    public void ShapesThatDoNotTouchAreLeftAsTheyAre()
    {
        var merged = PathBooleans.Union([Square(0, 0, 10), Square(50, 50, 10)]);

        Assert.Equal(2, merged.Count);
        Assert.Equal(200, merged.Sum(p => p.Bounds.Width * p.Bounds.Height), 0);
    }

    [Fact]
    public void HolesSurvive()
    {
        // A washer is two contours, and both are real boundaries. Losing the inner
        // one would fill in the middle; losing the outer one is nonsense.
        var merged = PathBooleans.Union([Circle(0, 0, 20), Circle(0, 0, 10)]);

        Assert.Equal(2, merged.Count);

        var radii = merged.Select(p => p.Bounds.Width / 2).OrderBy(r => r).ToList();
        Assert.Equal(10, radii[0], 0);
        Assert.Equal(20, radii[1], 0);
    }

    [Fact]
    public void LetterCountersAreKept()
    {
        // The hole in an "o" is the same problem as a washer, and the case that
        // shows up every time somebody engraves text.
        var outer = Square(0, 0, 30);
        var counter = Square(10, 10, 10);

        var merged = PathBooleans.Union([outer, counter]);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, p => Math.Abs(p.Bounds.Width - 10) < 0.5);
        Assert.Contains(merged, p => Math.Abs(p.Bounds.Width - 30) < 0.5);
    }

    [Fact]
    public void OpenPathsArePassedThroughUntouched()
    {
        // A score line is a line, not a region. Merging it into anything is meaningless.
        var score = new Polyline([new Vec2(-30, 5), new Vec2(30, 5)]);

        var merged = PathBooleans.Union([Circle(0, 0, 10), Circle(12, 0, 10), score]);

        Assert.Contains(merged, p => !p.IsClosed && p.Count == 2);
    }

    [Fact]
    public void ASinglePathIsReturnedUnchanged()
    {
        var one = Circle(0, 0, 10);
        var merged = PathBooleans.Union([one]);

        Assert.Single(merged);
        Assert.Equal(one.Count, merged[0].Count);
    }

    [Fact]
    public void NothingInComesToNothingOut()
    {
        Assert.Empty(PathBooleans.Union([]));
    }

    [Fact]
    public void OverlapDetectionAnswersHonestly()
    {
        Assert.True(PathBooleans.AnyOverlap([Square(0, 0, 20), Square(10, 10, 20)]));
        Assert.False(PathBooleans.AnyOverlap([Square(0, 0, 10), Square(50, 50, 10)]));
        Assert.False(PathBooleans.AnyOverlap([Square(0, 0, 10)]));
    }

    [Fact]
    public void ThreeShapesInAChainBecomeOne()
    {
        var merged = PathBooleans.Union([Square(0, 0, 20), Square(15, 0, 20), Square(30, 0, 20)]);

        Assert.Single(merged);
        Assert.Equal(50, merged[0].Bounds.Width, 0);
    }
}

/// <summary>
/// The end of the chain: merging has to change the G-code, not just the geometry.
/// </summary>
public class MergedCutJobTests
{
    private static PathShape Square(double x, double y, double size) => new([new Polyline(
    [
        new Vec2(x, y), new Vec2(x + size, y), new Vec2(x + size, y + size), new Vec2(x, y + size),
    ], closed: true)]);

    private static (Design Design, Layer Layer) Overlapping(bool merge)
    {
        var design = new Design { Name = "overlap" };
        var layer = Layer.CreateDefault(OperationKind.Cut, 0);
        layer.Passes = 1;
        layer.MergeOverlaps = merge;
        design.Layers.Add(layer);

        design.AddShape(Square(100, 100, 40), layer);
        design.AddShape(Square(120, 100, 40), layer);
        return (design, layer);
    }

    private static string Generate(Design design)
    {
        var machine = MachineLibrary.Load().Profiles.First(p => p.Id == "generic-grbl");
        return string.Join("\n", CamPipeline.Generate(design, machine, new CamOptions()).Job.Lines);
    }

    /// <summary>Is there a burning move straight down this x, of any length?</summary>
    private static bool HasVerticalBurnAt(GCode.Toolpath toolpath, double x)
    {
        for (var i = 0; i < toolpath.Count; i++)
        {
            if (toolpath.Power[i] <= 0) continue;
            if (Math.Abs(toolpath.X0[i] - toolpath.X1[i]) > 0.01) continue;
            if (Math.Abs(toolpath.Y0[i] - toolpath.Y1[i]) < 0.5) continue;
            if (Math.Abs(toolpath.X0[i] - x) < 0.5) return true;
        }
        return false;
    }

    [Fact]
    public void MergingIsOnByDefaultForACutLayer()
    {
        Assert.True(Layer.CreateDefault(OperationKind.Cut, 0).MergeOverlaps);
    }

    [Fact]
    public void MergedOverlappingSquaresCutLessThanSeparateOnes()
    {
        // Two 40 mm squares overlapping by 20 mm: cut separately that is 320 mm of
        // boundary, merged it is 240 mm. The difference is the two lines through
        // the middle that would have cut the piece apart.
        var (merged, _) = Overlapping(merge: true);
        var (separate, _) = Overlapping(merge: false);

        var machine = MachineLibrary.Load().Profiles.First(p => p.Id == "generic-grbl");
        var a = CamPipeline.Generate(merged, machine, new CamOptions());
        var b = CamPipeline.Generate(separate, machine, new CamOptions());

        Assert.True(a.Toolpath.BurnBounds.Width > 0);
        Assert.True(a.Estimate.Total < b.Estimate.Total,
            $"merged {a.Estimate.Total} should be quicker than separate {b.Estimate.Total}");
    }

    [Fact]
    public void NothingCutsThroughTheSharedInterior()
    {
        var (design, _) = Overlapping(merge: true);
        var machine = MachineLibrary.Load().Profiles.First(p => p.Id == "generic-grbl");
        var result = CamPipeline.Generate(design, machine, new CamOptions());

        // Squares at 100..140 and 120..160, so the two edges that would cut the
        // merged piece apart sit exactly at x=120 and x=140. The union is a single
        // 100..160 rectangle, and neither interior edge should be burned.
        Assert.False(HasVerticalBurnAt(result.Toolpath, 120), "the beam runs down the interior edge at x=120");
        Assert.False(HasVerticalBurnAt(result.Toolpath, 140), "the beam runs down the interior edge at x=140");

        // The outer edges must still be there, or nothing was cut at all.
        Assert.True(HasVerticalBurnAt(result.Toolpath, 100));
        Assert.True(HasVerticalBurnAt(result.Toolpath, 160));
    }

    [Fact]
    public void TurningItOffKeepsTheCrossingLines()
    {
        // Sometimes the crossing lines are the point — a scored grid, or pieces
        // meant to be cut apart.
        var (design, _) = Overlapping(merge: false);
        var machine = MachineLibrary.Load().Profiles.First(p => p.Id == "generic-grbl");
        var result = CamPipeline.Generate(design, machine, new CamOptions());

        Assert.True(HasVerticalBurnAt(result.Toolpath, 120) && HasVerticalBurnAt(result.Toolpath, 140),
            "with merging off, the original outlines including their interior edges should still be cut");
    }
}
