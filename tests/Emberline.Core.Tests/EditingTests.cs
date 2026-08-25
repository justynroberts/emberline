using Emberline.Core.Documents;
using Emberline.Core.Geometry;
using Xunit;

namespace Emberline.Core.Tests;

public class UndoStackTests
{
    [Fact]
    public void UndoAndRedoRestoreState()
    {
        var stack = new UndoStack();
        var value = 0;

        value = 5;
        stack.Push("set", () => value = 0, () => value = 5);

        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);

        stack.Undo();
        Assert.Equal(0, value);
        Assert.True(stack.CanRedo);

        stack.Redo();
        Assert.Equal(5, value);
    }

    [Fact]
    public void ANewEditDiscardsTheRedoBranch()
    {
        var stack = new UndoStack();
        stack.Push("a", () => { }, () => { });
        stack.Undo();
        Assert.True(stack.CanRedo);

        stack.Push("b", () => { }, () => { });
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void UndoingDoesNotRecordItself()
    {
        // A naive implementation pushes the inverse while undoing and loops forever.
        var stack = new UndoStack();
        var applied = 0;

        stack.Push("edit",
            () => { applied--; stack.Push("nested", () => { }, () => { }); },
            () => applied++);

        stack.Undo();

        Assert.Equal(-1, applied);
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void CoalescingKeepsADragToOneHistoryEntry()
    {
        // Without this, one drag fills the history and undo moves a pixel at a time.
        var stack = new UndoStack();
        var position = 0;

        for (var i = 1; i <= 50; i++)
        {
            var target = i;
            stack.PushOrMerge("Move", "drag-1", () => position = 0, () => position = target);
            position = target;
        }

        Assert.Equal(50, position);

        stack.Undo();
        Assert.Equal(0, position);
        Assert.False(stack.CanUndo);

        stack.Redo();
        Assert.Equal(50, position);
    }

    [Fact]
    public void EndingAMergeStartsANewEntry()
    {
        var stack = new UndoStack();
        stack.PushOrMerge("Move", "drag-1", () => { }, () => { });
        stack.EndMerge();
        stack.PushOrMerge("Move", "drag-1", () => { }, () => { });

        stack.Undo();
        Assert.True(stack.CanUndo);
    }

    [Fact]
    public void TheHistoryIsBounded()
    {
        var stack = new UndoStack(limit: 5);
        for (var i = 0; i < 20; i++) stack.Push($"edit {i}", () => { }, () => { });

        var count = 0;
        while (stack.Undo()) count++;

        Assert.Equal(5, count);
    }

    [Fact]
    public void CaptureTransformsRestoresEveryShape()
    {
        var a = PathShape.Rectangle(10, 10);
        var b = PathShape.Rectangle(20, 20);
        b.Translate(new Vec2(50, 50));

        var restore = UndoStack.CaptureTransforms([a, b]);

        a.Translate(new Vec2(100, 0));
        b.Translate(new Vec2(0, 100));
        Assert.Equal(100, a.Bounds.MinX, 6);

        restore();
        Assert.Equal(0, a.Bounds.MinX, 6);
        Assert.Equal(50, b.Bounds.MinX, 6);
    }

    [Fact]
    public void NamesAreReportedForTheMenu()
    {
        var stack = new UndoStack();
        Assert.Null(stack.UndoName);

        stack.Push("Rotate", () => { }, () => { });
        Assert.Equal("Rotate", stack.UndoName);

        stack.Undo();
        Assert.Equal("Rotate", stack.RedoName);
    }
}

public class ArrangeTests
{
    private static PathShape At(double x, double y, double w, double h)
    {
        var shape = PathShape.Rectangle(w, h);
        shape.MoveTo(new Vec2(x, y));
        return shape;
    }

    [Fact]
    public void AlignLeftLinesUpTheLeftEdges()
    {
        var a = At(10, 0, 20, 10);
        var b = At(50, 20, 30, 10);
        var c = At(35, 40, 10, 10);

        Arrange.Align([a, b, c], AlignEdge.Left);

        Assert.Equal(10, a.Bounds.MinX, 6);
        Assert.Equal(10, b.Bounds.MinX, 6);
        Assert.Equal(10, c.Bounds.MinX, 6);
    }

    [Fact]
    public void AlignRightUsesTheRightEdgesNotTheOrigins()
    {
        var narrow = At(0, 0, 10, 10);
        var wide = At(0, 20, 40, 10);

        Arrange.Align([narrow, wide], AlignEdge.Right);

        Assert.Equal(40, narrow.Bounds.MaxX, 6);
        Assert.Equal(40, wide.Bounds.MaxX, 6);
    }

    [Fact]
    public void AlignCentreCentresOnTheCombinedBounds()
    {
        var a = At(0, 0, 20, 10);
        var b = At(60, 20, 20, 10);

        Arrange.Align([a, b], AlignEdge.HorizontalCentre);

        Assert.Equal(a.Bounds.Center.X, b.Bounds.Center.X, 6);
        Assert.Equal(40, a.Bounds.Center.X, 6);
    }

    [Fact]
    public void AlignRelativeToTheBedWorksForASingleShape()
    {
        var shape = At(10, 10, 20, 20);
        var bed = new Rect2(0, 0, 400, 300);

        Arrange.Align([shape], AlignEdge.HorizontalCentre, bed);

        Assert.Equal(200, shape.Bounds.Center.X, 6);
        Assert.Equal(20, shape.Bounds.Center.Y, 6);
    }

    [Fact]
    public void LockedShapesAreNotMoved()
    {
        var locked = At(100, 0, 10, 10);
        locked.Locked = true;
        var free = At(0, 0, 10, 10);

        Arrange.Align([locked, free], AlignEdge.Left);

        Assert.Equal(100, locked.Bounds.MinX, 6);
    }

    [Fact]
    public void DistributeEvensTheGapsAndLeavesTheEndsAlone()
    {
        var a = At(0, 0, 10, 10);
        var b = At(15, 0, 10, 10);
        var c = At(100, 0, 10, 10);

        Arrange.Distribute([a, b, c], DistributeAxis.Horizontal);

        Assert.Equal(0, a.Bounds.MinX, 6);
        Assert.Equal(110, c.Bounds.MaxX, 6);

        var gap1 = b.Bounds.MinX - a.Bounds.MaxX;
        var gap2 = c.Bounds.MinX - b.Bounds.MaxX;
        Assert.Equal(gap1, gap2, 6);
    }

    [Fact]
    public void DistributeNeedsThreeShapes()
    {
        var a = At(0, 0, 10, 10);
        var b = At(50, 0, 10, 10);

        Arrange.Distribute([a, b], DistributeAxis.Horizontal);

        Assert.Equal(0, a.Bounds.MinX, 6);
        Assert.Equal(50, b.Bounds.MinX, 6);
    }

    [Fact]
    public void ArrayProducesAGridWithTheOriginalInPlace()
    {
        var source = At(10, 10, 20, 10);
        var copies = Arrange.Array(source, columns: 3, rows: 2, spacingXMm: 5, spacingYMm: 5);

        // Three by two is six, minus the original which does not move.
        Assert.Equal(5, copies.Count);
        Assert.Equal(10, source.Bounds.MinX, 6);

        var all = new List<Shape> { source };
        all.AddRange(copies);
        var bounds = Arrange.Bounds(all);

        // Three columns of 20 mm with two 5 mm gaps; two rows of 10 mm with one gap.
        Assert.Equal(70, bounds.Width, 6);
        Assert.Equal(25, bounds.Height, 6);
    }

    [Fact]
    public void PlaceOnEachCentresACopyOnEveryTarget()
    {
        var source = At(0, 0, 20, 20);
        var centres = new List<Vec2> { new(50, 50), new(150, 50), new(50, 150), new(150, 150) };

        var copies = Arrange.PlaceOnEach(source, centres);

        Assert.Equal(4, copies.Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(centres[i].X, copies[i].Bounds.Center.X, 6);
            Assert.Equal(centres[i].Y, copies[i].Bounds.Center.Y, 6);
        }
    }

    [Fact]
    public void ScalingASelectionKeepsRelativePositions()
    {
        var a = At(0, 0, 10, 10);
        var b = At(20, 0, 10, 10);
        var anchor = new Vec2(0, 0);

        Arrange.ScaleSelection([a, b], 2, 2, anchor);

        Assert.Equal(0, a.Bounds.MinX, 6);
        Assert.Equal(20, a.Bounds.Width, 6);
        Assert.Equal(40, b.Bounds.MinX, 6);
    }

    [Fact]
    public void RotatingASelectionTurnsItAsAUnit()
    {
        var a = At(0, 0, 10, 10);
        var b = At(90, 0, 10, 10);
        var pivot = Arrange.Bounds([a, b]).Center;

        Arrange.RotateSelection([a, b], 90, pivot);

        // A quarter turn about the shared centre swaps the axis they are spread on.
        var bounds = Arrange.Bounds([a, b]);
        Assert.Equal(10, bounds.Width, 4);
        Assert.Equal(100, bounds.Height, 4);
    }
}

public class SelectionMathTests
{
    private static readonly Rect2 Box = new(10, 10, 50, 30);

    [Theory]
    [InlineData(SelectionHandle.ScaleBottomLeft, 50.0, 30.0)]
    [InlineData(SelectionHandle.ScaleTopRight, 10.0, 10.0)]
    [InlineData(SelectionHandle.ScaleLeft, 50.0, 20.0)]
    [InlineData(SelectionHandle.ScaleTop, 30.0, 10.0)]
    public void TheAnchorIsTheOppositeCorner(SelectionHandle handle, double x, double y)
    {
        var anchor = SelectionMath.AnchorFor(handle, Box);
        Assert.Equal(x, anchor.X, 6);
        Assert.Equal(y, anchor.Y, 6);
    }

    [Fact]
    public void DraggingACornerToTwiceTheDistanceDoublesTheScale()
    {
        var anchor = SelectionMath.AnchorFor(SelectionHandle.ScaleTopRight, Box);
        // Box is 40 wide, 20 tall from the anchor at (10, 10).
        var (x, y) = SelectionMath.ScaleFactors(SelectionHandle.ScaleTopRight, Box, anchor, new Vec2(90, 50), uniform: false);

        Assert.Equal(2, x, 6);
        Assert.Equal(2, y, 6);
    }

    [Fact]
    public void EdgeHandlesConstrainToOneAxis()
    {
        var anchor = SelectionMath.AnchorFor(SelectionHandle.ScaleRight, Box);
        var (x, y) = SelectionMath.ScaleFactors(SelectionHandle.ScaleRight, Box, anchor, new Vec2(90, 999), uniform: false);

        Assert.Equal(2, x, 6);
        Assert.Equal(1, y, 6);
    }

    [Fact]
    public void UniformScalingTakesTheLargerFactor()
    {
        var anchor = SelectionMath.AnchorFor(SelectionHandle.ScaleTopRight, Box);
        var (x, y) = SelectionMath.ScaleFactors(SelectionHandle.ScaleTopRight, Box, anchor, new Vec2(90, 30), uniform: true);

        Assert.Equal(x, y, 9);
        Assert.Equal(2, x, 6);
    }

    [Fact]
    public void ScaleIsClampedSoGeometryCannotCollapse()
    {
        var anchor = SelectionMath.AnchorFor(SelectionHandle.ScaleTopRight, Box);
        var (x, y) = SelectionMath.ScaleFactors(SelectionHandle.ScaleTopRight, Box, anchor, anchor, uniform: false);

        Assert.Equal(SelectionMath.MinimumScale, x, 9);
        Assert.Equal(SelectionMath.MinimumScale, y, 9);
    }

    [Fact]
    public void AngleBetweenMeasuresTheSweep()
    {
        var pivot = new Vec2(0, 0);
        Assert.Equal(90, SelectionMath.AngleBetween(pivot, new Vec2(10, 0), new Vec2(0, 10)), 6);
        Assert.Equal(-90, SelectionMath.AngleBetween(pivot, new Vec2(0, 10), new Vec2(10, 0)), 6);
    }

    [Theory]
    [InlineData(7, 0)]
    [InlineData(8, 15)]
    [InlineData(44, 45)]
    [InlineData(-44, -45)]
    public void AnglesSnapToFifteenDegrees(double input, double expected) =>
        Assert.Equal(expected, SelectionMath.SnapAngle(input), 6);

    [Fact]
    public void PointsSnapToTheGrid()
    {
        var snapped = SelectionMath.SnapToGrid(new Vec2(10.4, 19.7), 1);
        Assert.Equal(10, snapped.X, 6);
        Assert.Equal(20, snapped.Y, 6);

        var unsnapped = SelectionMath.SnapToGrid(new Vec2(10.4, 19.7), 0);
        Assert.Equal(10.4, unsnapped.X, 6);
    }

    [Fact]
    public void MarqueeDirectionChoosesEnclosedOrTouched()
    {
        var inside = PathShape.Rectangle(10, 10);
        inside.MoveTo(new Vec2(20, 20));

        var straddling = PathShape.Rectangle(40, 10);
        straddling.MoveTo(new Vec2(40, 20));

        var marquee = new Rect2(10, 10, 50, 50);

        var enclosed = SelectionMath.InMarquee([inside, straddling], marquee, requireFullyInside: true);
        Assert.Single(enclosed);
        Assert.Same(inside, enclosed[0]);

        var touched = SelectionMath.InMarquee([inside, straddling], marquee, requireFullyInside: false);
        Assert.Equal(2, touched.Count);
    }

    [Fact]
    public void LockedAndHiddenShapesAreNotMarqueeSelected()
    {
        var locked = PathShape.Rectangle(10, 10);
        locked.MoveTo(new Vec2(20, 20));
        locked.Locked = true;

        var hidden = PathShape.Rectangle(10, 10);
        hidden.MoveTo(new Vec2(30, 30));
        hidden.Visible = false;

        Assert.Empty(SelectionMath.InMarquee([locked, hidden], new Rect2(0, 0, 100, 100), false));
    }
}
