using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;
using OpenBurn.Core.Units;
using Xunit;

namespace OpenBurn.Core.Tests;

public class MatrixTests
{
    [Fact]
    public void IdentityLeavesPointsAlone()
    {
        var p = Matrix2D.Identity.Apply(new Vec2(3, 4));
        Assert.Equal(3, p.X, 9);
        Assert.Equal(4, p.Y, 9);
    }

    [Fact]
    public void MultiplicationAppliesTheRightHandSideFirst()
    {
        // Translate then scale is not the same as scale then translate; getting the
        // order wrong is how imported SVG groups end up in the wrong place.
        var scaleThenTranslate = Matrix2D.Translate(10, 0) * Matrix2D.Scale(2);
        Assert.Equal(20, scaleThenTranslate.Apply(new Vec2(5, 0)).X, 9);

        var translateThenScale = Matrix2D.Scale(2) * Matrix2D.Translate(10, 0);
        Assert.Equal(30, translateThenScale.Apply(new Vec2(5, 0)).X, 9);
    }

    [Fact]
    public void RotationAboutAPivotKeepsThePivotFixed()
    {
        var pivot = new Vec2(10, 10);
        var result = Matrix2D.RotateAbout(90, pivot).Apply(pivot);
        Assert.Equal(10, result.X, 9);
        Assert.Equal(10, result.Y, 9);
    }

    [Fact]
    public void NinetyDegreeRotationMapsXOntoY()
    {
        var p = Matrix2D.Rotate(90).Apply(new Vec2(1, 0));
        Assert.Equal(0, p.X, 9);
        Assert.Equal(1, p.Y, 9);
    }

    [Fact]
    public void InverseUndoesTheTransform()
    {
        var m = Matrix2D.Translate(5, 7) * Matrix2D.Rotate(33) * Matrix2D.Scale(2, 3);
        Assert.True(m.TryInvert(out var inverse));

        var original = new Vec2(11, -4);
        var roundTrip = inverse.Apply(m.Apply(original));
        Assert.Equal(original.X, roundTrip.X, 9);
        Assert.Equal(original.Y, roundTrip.Y, 9);
    }

    [Fact]
    public void SingularMatricesCannotBeInverted() =>
        Assert.False(Matrix2D.Scale(0, 0).TryInvert(out _));

    [Fact]
    public void ScaleFactorTracksUniformScaling() =>
        Assert.Equal(3, Matrix2D.Scale(3).ScaleFactor, 9);

    [Fact]
    public void ApplyVectorIgnoresTranslation()
    {
        var v = (Matrix2D.Translate(100, 100) * Matrix2D.Scale(2)).ApplyVector(new Vec2(1, 0));
        Assert.Equal(2, v.X, 9);
        Assert.Equal(0, v.Y, 9);
    }
}

public class RectTests
{
    [Fact]
    public void EmptyRectangleSwallowsTheFirstPoint()
    {
        var r = Rect2.Empty.Add(new Vec2(5, 5));
        Assert.False(r.IsEmpty);
        Assert.Equal(0, r.Width, 9);
        Assert.Equal(5, r.MinX, 9);
    }

    [Fact]
    public void UnionCoversBothInputs()
    {
        var r = Rect2.FromSize(0, 0, 10, 10).Union(Rect2.FromSize(20, 20, 5, 5));
        Assert.Equal(0, r.MinX, 9);
        Assert.Equal(25, r.MaxX, 9);
    }

    [Fact]
    public void ContainsIsInclusiveOfTheBoundary()
    {
        var r = Rect2.FromSize(0, 0, 10, 10);
        Assert.True(r.Contains(new Vec2(0, 0)));
        Assert.True(r.Contains(new Vec2(10, 10)));
        Assert.False(r.Contains(new Vec2(10.001, 5)));
    }

    [Fact]
    public void ContainsRectangleDetectsAJobLeavingTheBed()
    {
        var bed = Rect2.FromSize(0, 0, 400, 400);
        Assert.True(bed.Contains(Rect2.FromSize(10, 10, 100, 100)));
        Assert.False(bed.Contains(Rect2.FromSize(350, 350, 100, 100)));
        Assert.False(bed.Contains(Rect2.FromSize(-5, 10, 20, 20)));
    }
}

public class PolylineTests
{
    [Fact]
    public void DuplicateConsecutivePointsAreDropped()
    {
        // Zero-length moves are pure overhead in the planner.
        var p = new Polyline();
        p.Add(0, 0);
        p.Add(0, 0);
        p.Add(10, 0);
        Assert.Equal(2, p.Count);
    }

    [Fact]
    public void ClosedPolylineLengthIncludesTheClosingSegment()
    {
        var square = new Polyline([new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 10), new Vec2(0, 10)], closed: true);
        Assert.Equal(40, square.Length, 9);

        var open = new Polyline(square.Points, closed: false);
        Assert.Equal(30, open.Length, 9);
    }

    [Fact]
    public void SignedAreaGivesWindingDirection()
    {
        var ccw = new Polyline([new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 10), new Vec2(0, 10)], closed: true);
        Assert.Equal(100, ccw.SignedArea, 9);
        Assert.False(ccw.IsClockwise);
        Assert.True(ccw.Reversed().IsClockwise);
    }

    [Fact]
    public void ContainsUsesEvenOddCrossing()
    {
        var square = new Polyline([new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 10), new Vec2(0, 10)], closed: true);
        Assert.True(square.Contains(new Vec2(5, 5)));
        Assert.False(square.Contains(new Vec2(15, 5)));
    }

    [Fact]
    public void RotatingAClosedLoopToTheNearestVertexShortensTheApproach()
    {
        var square = new Polyline([new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 10), new Vec2(0, 10)], closed: true);
        var rotated = square.RotatedToNearest(new Vec2(11, 11));

        Assert.Equal(new Vec2(10, 10), rotated.First);
        Assert.Equal(square.Count, rotated.Count);
        Assert.Equal(square.Length, rotated.Length, 9);
    }
}

public class PathOpsTests
{
    [Fact]
    public void SimplifyRemovesCollinearPoints()
    {
        var line = new Polyline(Enumerable.Range(0, 50).Select(i => new Vec2(i, 0)));
        var simplified = PathOps.Simplify(line, 0.01);
        Assert.Equal(2, simplified.Count);
    }

    [Fact]
    public void SimplifyKeepsRealCorners()
    {
        var l = new Polyline([new Vec2(0, 0), new Vec2(5, 0), new Vec2(10, 0), new Vec2(10, 5), new Vec2(10, 10)]);
        var simplified = PathOps.Simplify(l, 0.01);
        Assert.Equal(3, simplified.Count);
    }

    [Fact]
    public void SimplifyHandlesVeryLongPathsWithoutStackOverflow()
    {
        // Recursive Douglas–Peucker dies on traced photographs; this must not.
        var points = new List<Vec2>();
        for (var i = 0; i < 200_000; i++) points.Add(new Vec2(i * 0.01, Math.Sin(i * 0.001) * 5));

        var simplified = PathOps.Simplify(new Polyline(points), 0.05);
        Assert.True(simplified.Count is > 2 and < 200_000);
    }

    [Fact]
    public void ConvexHullOfASquareWithInteriorPointsIsTheSquare()
    {
        var hull = PathOps.ConvexHull(
        [
            new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 10), new Vec2(0, 10),
            new Vec2(5, 5), new Vec2(3, 7), new Vec2(8, 2),
        ]);

        Assert.Equal(4, hull.Count);
        Assert.True(hull.IsClosed);
        Assert.Equal(40, hull.Length, 6);
    }

    [Fact]
    public void TravelOptimisationBeatsTheOriginalOrdering()
    {
        // Paths deliberately interleaved so the naive order zig-zags across the bed.
        var paths = new List<Polyline>();
        for (var i = 0; i < 12; i++)
        {
            var x = i % 2 == 0 ? i * 5.0 : 200 - i * 5.0;
            paths.Add(new Polyline([new Vec2(x, i * 3), new Vec2(x + 2, i * 3)]));
        }

        var before = PathOps.TravelCost(paths, Vec2.Zero);
        var after = PathOps.TravelCost(PathOps.OptimiseTravel(paths, Vec2.Zero), Vec2.Zero);

        Assert.True(after < before * 0.6, $"travel only fell from {before:0} to {after:0} mm");
    }

    [Fact]
    public void OptimisationKeepsEveryPath()
    {
        var paths = Enumerable.Range(0, 30)
            .Select(i => new Polyline([new Vec2(i * 7 % 100, i * 3), new Vec2(i * 7 % 100 + 4, i * 3)]))
            .ToList();

        Assert.Equal(30, PathOps.OptimiseTravel(paths, Vec2.Zero).Count);
    }

    [Fact]
    public void NestedContoursAreCutFromTheInsideOut()
    {
        // Cut the outside of a ring first and the part drops out mid-job.
        var outer = new Polyline([new Vec2(0, 0), new Vec2(100, 0), new Vec2(100, 100), new Vec2(0, 100)], closed: true);
        var inner = new Polyline([new Vec2(40, 40), new Vec2(60, 40), new Vec2(60, 60), new Vec2(40, 60)], closed: true);

        var ordered = PathOps.InsideOutFirst([outer, inner]);
        Assert.Same(inner, ordered[0]);
        Assert.Same(outer, ordered[1]);
    }

    [Fact]
    public void SmoothingAddsPointsAndShortensCorners()
    {
        var l = new Polyline([new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 10)]);
        var smoothed = PathOps.Smooth(l, 2);

        Assert.True(smoothed.Count > l.Count);
        Assert.True(smoothed.Length < l.Length);
    }
}

public class CurvesTests
{
    [Fact]
    public void ATighterToleranceProducesMoreSegments()
    {
        var coarse = new Polyline();
        Curves.FlattenCubic(coarse, Vec2.Zero, new Vec2(0, 50), new Vec2(100, 50), new Vec2(100, 0), 1.0);

        var fine = new Polyline();
        Curves.FlattenCubic(fine, Vec2.Zero, new Vec2(0, 50), new Vec2(100, 50), new Vec2(100, 0), 0.005);

        Assert.True(fine.Count > coarse.Count * 2);
    }

    [Fact]
    public void FlattenedArcLengthMatchesTheAnalyticValue()
    {
        var p = new Polyline();
        Curves.FlattenArc(p, Vec2.Zero, 10, 0, Math.PI * 2, 0.001);

        // Circumference of a radius-10 circle.
        Assert.Equal(62.832, p.Length, 1);
    }

    [Fact]
    public void QuadraticAndEquivalentCubicAgree()
    {
        var quad = new Polyline();
        Curves.FlattenQuadratic(quad, Vec2.Zero, new Vec2(50, 100), new Vec2(100, 0), 0.01);

        var cubic = new Polyline();
        Curves.FlattenCubic(cubic, Vec2.Zero,
            new Vec2(0, 0) + (new Vec2(50, 100) - new Vec2(0, 0)) * (2.0 / 3.0),
            new Vec2(100, 0) + (new Vec2(50, 100) - new Vec2(100, 0)) * (2.0 / 3.0),
            new Vec2(100, 0), 0.01);

        Assert.Equal(cubic.Length, quad.Length, 3);
    }

    [Fact]
    public void SvgArcRadiiAreScaledUpWhenTooSmallToSpanTheEndpoints()
    {
        // The spec says to grow the radii rather than fail.
        var p = new Polyline();
        p.Add(Vec2.Zero);
        Curves.FlattenSvgArc(p, Vec2.Zero, new Vec2(100, 0), 10, 10, 0, false, true, 0.01);

        Assert.Equal(100, p.Last.X, 6);
        Assert.True(p.Count > 5);
    }
}

public class UnitTests
{
    [Theory]
    [InlineData(25.4, LengthUnit.Inches, 1.0)]
    [InlineData(100, LengthUnit.Millimetres, 100.0)]
    public void ConvertsFromMillimetres(double mm, LengthUnit unit, double expected) =>
        Assert.Equal(expected, UnitConvert.FromMm(mm, unit), 9);

    [Fact]
    public void RoundTripsThroughInches()
    {
        var mm = UnitConvert.ToMm(UnitConvert.FromMm(123.456, LengthUnit.Inches), LengthUnit.Inches);
        Assert.Equal(123.456, mm, 9);
    }

    [Theory]
    [InlineData(0.1, 254.0)]
    [InlineData(0.0254, 1000.0)]
    public void ConvertsLineIntervalToDpi(double interval, double expectedDpi) =>
        Assert.Equal(expectedDpi, UnitConvert.IntervalToDpi(interval), 3);
}

public class DocumentTests
{
    [Fact]
    public void ANewDesignHasAnEngraveAndACutLayer()
    {
        var design = Design.CreateDefault();
        Assert.Equal(2, design.Layers.Count);
        Assert.Contains(design.Layers, l => l.Operation == OperationKind.Engrave);
        Assert.Contains(design.Layers, l => l.Operation == OperationKind.Cut);
    }

    [Fact]
    public void ShapeBoundsFollowTheTransform()
    {
        var shape = PathShape.Rectangle(20, 10);
        Assert.Equal(20, shape.Bounds.Width, 6);

        shape.Transform = Matrix2D.Translate(100, 50);
        Assert.Equal(100, shape.Bounds.MinX, 6);
        Assert.Equal(50, shape.Bounds.MinY, 6);
    }

    [Fact]
    public void RotatedBoundsUseTheTransformedCorners()
    {
        // A 45° rotated 10 mm square has a bounding box of 10·√2.
        var shape = PathShape.Rectangle(10, 10);
        shape.RotateAbout(45, new Vec2(5, 5));
        Assert.Equal(14.142, shape.Bounds.Width, 2);
    }

    [Fact]
    public void ResizeToKeepsAspectWhenAsked()
    {
        var shape = PathShape.Rectangle(20, 10);
        shape.ResizeTo(40, 40, keepAspect: true);

        Assert.Equal(40, shape.Bounds.Width, 6);
        Assert.Equal(20, shape.Bounds.Height, 6);
    }

    [Fact]
    public void MoveToPlacesTheBottomLeftCorner()
    {
        var shape = PathShape.Rectangle(20, 10);
        shape.MoveTo(new Vec2(35, 45));

        Assert.Equal(35, shape.Bounds.MinX, 6);
        Assert.Equal(45, shape.Bounds.MinY, 6);
    }

    [Fact]
    public void UngroupingBakesTheGroupTransformIntoChildren()
    {
        var child = PathShape.Rectangle(10, 10);
        var group = new GroupShape([child]) { Transform = Matrix2D.Translate(50, 50) };

        var children = group.Ungroup();
        Assert.Single(children);
        Assert.Equal(50, children[0].Bounds.MinX, 6);
    }

    [Fact]
    public void DeletingALayerMovesItsShapesRatherThanLosingThem()
    {
        var design = Design.CreateDefault();
        var doomed = design.Layers[1];
        var shape = PathShape.Rectangle(10, 10);
        design.AddShape(shape, doomed);

        design.RemoveLayer(doomed);

        Assert.Single(design.Layers);
        Assert.Contains(shape, design.Shapes);
        Assert.Equal(design.Layers[0].Id, shape.LayerId);
    }

    [Fact]
    public void CloningADesignRemapsShapesOntoTheClonedLayers()
    {
        var design = Design.CreateDefault();
        design.AddShape(PathShape.Rectangle(10, 10), design.Layers[1]);

        var copy = design.Clone();

        Assert.Equal(design.Shapes.Count, copy.Shapes.Count);
        Assert.NotEqual(design.Layers[1].Id, copy.Layers[1].Id);
        Assert.Equal(copy.Layers[1].Id, copy.Shapes[0].LayerId);
    }

    [Fact]
    public void MachineProfilesConvertPercentagesToSValues()
    {
        var machine = MachineProfileFixture();
        Assert.Equal(500, machine.PowerToSpindle(50));
        Assert.Equal(1000, machine.PowerToSpindle(150)); // clamped
        Assert.Equal(0, machine.PowerToSpindle(-10));
    }

    private static Core.Machines.MachineProfile MachineProfileFixture() =>
        Core.Machines.MachineProfile.GenericGrbl() with { MaxSpindleValue = 1000 };
}

public class JobLibraryTests
{
    private static Core.Jobs.JobRecord Record(string name, string material, Core.Jobs.JobState outcome, DateTimeOffset started) => new()
    {
        Name = name,
        StartedAt = started,
        FinishedAt = started.AddMinutes(4),
        Outcome = outcome,
        MachineName = "BlazeX M5 Pro 10W",
        MaterialName = material,
        SpeedMmMin = 3000,
        PowerPercent = 25,
        Passes = 1,
        TotalLines = 1200,
        LinesCompleted = 1200,
        WidthMm = 80,
        HeightMm = 60,
    };

    [Fact]
    public void RecordsAndReadsBackAJob()
    {
        using var library = Core.Storage.JobLibrary.InMemory();
        var record = Record("Coaster", "Slate coaster", Core.Jobs.JobState.Completed, DateTimeOffset.UtcNow);

        library.Record(record);

        var found = library.Find(record.Id);
        Assert.NotNull(found);
        Assert.Equal("Coaster", found!.Name);
        Assert.Equal(Core.Jobs.JobState.Completed, found.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(4), found.Duration);
    }

    [Fact]
    public void RecordingTheSameIdTwiceUpdatesRatherThanDuplicating()
    {
        using var library = Core.Storage.JobLibrary.InMemory();
        var record = Record("Sign", "Plywood", Core.Jobs.JobState.Running, DateTimeOffset.UtcNow);

        library.Record(record);
        library.Record(record with { Outcome = Core.Jobs.JobState.Completed, LinesCompleted = 1200 });

        Assert.Equal(1, library.Count());
        Assert.Equal(Core.Jobs.JobState.Completed, library.Find(record.Id)!.Outcome);
    }

    [Fact]
    public void RecentReturnsNewestFirst()
    {
        using var library = Core.Storage.JobLibrary.InMemory();
        var now = DateTimeOffset.UtcNow;

        library.Record(Record("Old", "Plywood", Core.Jobs.JobState.Completed, now.AddDays(-3)));
        library.Record(Record("New", "Plywood", Core.Jobs.JobState.Completed, now));

        var recent = library.Recent();
        Assert.Equal("New", recent[0].Name);
        Assert.Equal("Old", recent[1].Name);
    }

    [Fact]
    public void FindsTheLastSettingsThatActuallyWorked()
    {
        // The point of the library: what worked on this machine beats any table.
        using var library = Core.Storage.JobLibrary.InMemory();
        var now = DateTimeOffset.UtcNow;

        library.Record(Record("Attempt", "Basswood", Core.Jobs.JobState.Failed, now.AddHours(-2)));
        library.Record(Record("Good one", "Basswood", Core.Jobs.JobState.Completed, now.AddHours(-1)) with { PowerPercent = 32 });
        library.Record(Record("Other material", "Slate coaster", Core.Jobs.JobState.Completed, now));

        var best = library.LastSuccessfulFor("Basswood");
        Assert.NotNull(best);
        Assert.Equal("Good one", best!.Name);
        Assert.Equal(32, best.PowerPercent);
    }

    [Fact]
    public void SearchesAcrossNameMaterialAndMachine()
    {
        using var library = Core.Storage.JobLibrary.InMemory();
        library.Record(Record("Dog coaster", "Slate coaster", Core.Jobs.JobState.Completed, DateTimeOffset.UtcNow));

        Assert.Single(library.Search("dog"));
        Assert.Single(library.Search("slate"));
        Assert.Single(library.Search("BlazeX"));
        Assert.Empty(library.Search("nothing like this"));
    }
}
