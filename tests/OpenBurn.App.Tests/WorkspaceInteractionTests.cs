using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using OpenBurn.App.Controls;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;
using OpenBurn.Core.Machines;
using Xunit;

// Avalonia has its own Design and Shape types.
using Design = OpenBurn.Core.Documents.Design;
using Shape = OpenBurn.Core.Documents.Shape;

namespace OpenBurn.App.Tests;

/// <summary>
/// Direct manipulation on the canvas, driven through real pointer events.
///
/// Everything here goes through the control's own input handling — press, move,
/// release — so it covers hit testing, pointer capture and the drag state machine
/// as well as the geometry. A unit test of the maths alone would pass while
/// dragging did nothing.
/// </summary>
public class WorkspaceInteractionTests
{
    /// <summary>
    /// A window containing only the canvas, with the view transform pinned so a
    /// test can convert between millimetres and pixels by hand.
    /// </summary>
    private sealed class Harness
    {
        public Window Window { get; }
        public WorkspaceView View { get; }
        public Design Design { get; } = Design.CreateDefault();
        public List<Shape> Selection { get; } = [];
        public int EditsBegun { get; private set; }
        public int EditsEnded { get; private set; }

        public Harness()
        {
            View = new WorkspaceView
            {
                Machine = MachineProfile.GenericGrbl() with { BedWidthMm = 400, BedHeightMm = 400 },
                Design = Design,
                SnapEnabled = false,
            };

            View.SelectionRequested += (shapes, additive) =>
            {
                if (!additive) Selection.Clear();
                foreach (var s in shapes)
                {
                    if (!Selection.Contains(s)) Selection.Add(s);
                }
                View.Selection = Selection.ToList();
            };

            View.EditBegan += _ => EditsBegun++;
            View.EditEnded += () => EditsEnded++;

            Window = new Window { Width = 800, Height = 600, Content = View };
            Window.Show();

            // Force layout so Bounds is real before any hit testing happens.
            Window.Measure(new Size(800, 600));
            Window.Arrange(new Rect(0, 0, 800, 600));
            Dispatcher.UIThread.RunJobs();

            View.ZoomToFitBed();
        }

        public Shape AddRect(double x, double y, double w, double h)
        {
            var shape = PathShape.Rectangle(w, h);
            shape.MoveTo(new Vec2(x, y));
            Design.AddShape(shape, Design.Layers[0]);
            return shape;
        }

        public Point Pixels(double xMm, double yMm) => View.ToPixels(xMm, yMm);

        public void Select(params Shape[] shapes)
        {
            Selection.Clear();
            Selection.AddRange(shapes);
            View.Selection = Selection.ToList();
        }

        /// <summary>Press, move in steps, release. Several moves, because one is not a drag.</summary>
        public void Drag(Point from, Point to, RawInputModifiers modifiers = RawInputModifiers.None, int steps = 4)
        {
            Window.MouseDown(from, MouseButton.Left, modifiers);

            for (var i = 1; i <= steps; i++)
            {
                var t = (double)i / steps;
                Window.MouseMove(new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t), modifiers: modifiers);
            }

            Window.MouseUp(to, MouseButton.Left, modifiers);
            Dispatcher.UIThread.RunJobs();
        }

        public void Click(Point at, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            Window.MouseDown(at, MouseButton.Left, modifiers);
            Window.MouseUp(at, MouseButton.Left, modifiers);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ClickingAShapeSelectsIt()
    {
        var h = new Harness();
        var shape = h.AddRect(100, 100, 60, 40);

        h.Click(h.Pixels(130, 120));

        Assert.Single(h.Selection);
        Assert.Same(shape, h.Selection[0]);
    }

    [AvaloniaFact]
    public void ClickingEmptySpaceClearsTheSelection()
    {
        var h = new Harness();
        var shape = h.AddRect(100, 100, 60, 40);
        h.Select(shape);

        h.Click(h.Pixels(320, 320));

        Assert.Empty(h.Selection);
    }

    [AvaloniaFact]
    public void ShiftClickingAddsToTheSelection()
    {
        var h = new Harness();
        var a = h.AddRect(50, 50, 40, 40);
        var b = h.AddRect(200, 200, 40, 40);

        h.Click(h.Pixels(70, 70));
        h.Click(h.Pixels(220, 220), RawInputModifiers.Shift);

        Assert.Equal(2, h.Selection.Count);
        Assert.Contains(a, h.Selection);
        Assert.Contains(b, h.Selection);
    }

    [AvaloniaFact]
    public void DraggingAShapeMovesIt()
    {
        var h = new Harness();
        var shape = h.AddRect(100, 100, 60, 40);
        h.Select(shape);

        h.Drag(h.Pixels(130, 120), h.Pixels(230, 220));

        Assert.Equal(200, shape.Bounds.MinX, 0);
        Assert.Equal(200, shape.Bounds.MinY, 0);
        Assert.Equal(60, shape.Bounds.Width, 3);
    }

    [AvaloniaFact]
    public void ADragRaisesExactlyOneEditBeginAndOneEditEnd()
    {
        // Undo depends on this: one snapshot per drag, not one per mouse-move.
        var h = new Harness();
        var shape = h.AddRect(100, 100, 60, 40);
        h.Select(shape);

        h.Drag(h.Pixels(130, 120), h.Pixels(200, 200), steps: 12);

        Assert.Equal(1, h.EditsBegun);
        Assert.Equal(1, h.EditsEnded);
    }

    [AvaloniaFact]
    public void DraggingMovesEverySelectedShapeTogether()
    {
        var h = new Harness();
        var a = h.AddRect(50, 50, 40, 40);
        var b = h.AddRect(150, 50, 40, 40);
        h.Select(a, b);

        h.Drag(h.Pixels(70, 70), h.Pixels(120, 70));

        Assert.Equal(100, a.Bounds.MinX, 0);
        Assert.Equal(200, b.Bounds.MinX, 0);
    }

    [AvaloniaFact]
    public void ALockedShapeDoesNotMove()
    {
        var h = new Harness();
        var shape = h.AddRect(100, 100, 60, 40);
        shape.Locked = true;
        h.Select(shape);

        h.Drag(h.Pixels(130, 120), h.Pixels(230, 220));

        Assert.Equal(100, shape.Bounds.MinX, 3);
    }

    [AvaloniaFact]
    public void DraggingACornerHandleResizes()
    {
        var h = new Harness();
        var shape = h.AddRect(100, 100, 100, 100);
        h.Select(shape);

        // The top-right handle in bed space is (200, 200); the anchor opposite is
        // the bottom-left at (100, 100). Dragging to (300, 300) doubles both sides.
        var handle = h.Pixels(200, 200);
        h.Drag(new Point(handle.X + 3, handle.Y - 3), h.Pixels(300, 300));

        Assert.Equal(200, shape.Bounds.Width, 0);
        Assert.Equal(200, shape.Bounds.Height, 0);
        Assert.Equal(100, shape.Bounds.MinX, 0);
        Assert.Equal(100, shape.Bounds.MinY, 0);
    }

    [AvaloniaFact]
    public void DraggingAnEdgeHandleResizesOneAxisOnly()
    {
        var h = new Harness();
        var shape = h.AddRect(100, 100, 100, 100);
        h.Select(shape);

        var handle = h.Pixels(200, 150);
        h.Drag(new Point(handle.X + 3, handle.Y), h.Pixels(300, 150));

        Assert.Equal(200, shape.Bounds.Width, 0);
        Assert.Equal(100, shape.Bounds.Height, 0);
    }

    [AvaloniaFact]
    public void DraggingTheRotateHandleTurnsTheSelection()
    {
        var h = new Harness();
        // A wide, short rectangle so a quarter turn is unmistakable.
        var shape = h.AddRect(100, 150, 120, 40);
        h.Select(shape);

        var boxTop = h.Pixels(160, 190);
        var rotateHandle = new Point(boxTop.X, boxTop.Y - SelectionInteraction.RotateHandleOffset - 3);

        // Drag the handle round to the right of the centre — a quarter turn.
        var centre = h.Pixels(160, 170);
        h.Drag(rotateHandle, new Point(centre.X + 120, centre.Y), steps: 10);

        Assert.Equal(40, shape.Bounds.Width, 0);
        Assert.Equal(120, shape.Bounds.Height, 0);
    }

    [AvaloniaFact]
    public void MarqueeDraggedLeftToRightSelectsOnlyEnclosedShapes()
    {
        var h = new Harness();
        var inside = h.AddRect(60, 60, 30, 30);
        var straddling = h.AddRect(150, 60, 120, 30);

        // Empty space at the top-left, dragging down and right.
        h.Drag(h.Pixels(40, 140), h.Pixels(200, 40), steps: 6);

        Assert.Single(h.Selection);
        Assert.Same(inside, h.Selection[0]);
        Assert.DoesNotContain(straddling, h.Selection);
    }

    [AvaloniaFact]
    public void MarqueeDraggedRightToLeftCatchesAnythingTouched()
    {
        var h = new Harness();
        var inside = h.AddRect(60, 60, 30, 30);
        var straddling = h.AddRect(150, 60, 120, 30);

        h.Drag(h.Pixels(200, 40), h.Pixels(40, 140), steps: 6);

        Assert.Equal(2, h.Selection.Count);
        Assert.Contains(inside, h.Selection);
        Assert.Contains(straddling, h.Selection);
    }

    [AvaloniaFact]
    public void SnappingRoundsTheShapeToTheGridNotTheCursor()
    {
        var h = new Harness();
        h.View.SnapEnabled = true;
        h.View.SnapMm = 10;

        var shape = h.AddRect(100, 100, 60, 40);
        h.Select(shape);

        // Aim for a deliberately unaligned destination.
        h.Drag(h.Pixels(130, 120), h.Pixels(187, 173));

        Assert.Equal(0, shape.Bounds.MinX % 10, 3);
        Assert.Equal(0, shape.Bounds.MinY % 10, 3);
    }

    [AvaloniaFact]
    public void ZoomingKeepsThePointUnderTheCursorStill()
    {
        var h = new Harness();
        var pivot = new Point(300, 250);
        var before = h.View.ToMillimetres(pivot);

        h.View.ZoomBy(1.8, pivot);
        var after = h.View.ToMillimetres(pivot);

        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);
    }

    [AvaloniaFact]
    public void MillimetresAndPixelsRoundTrip()
    {
        var h = new Harness();
        foreach (var (x, y) in new[] { (0.0, 0.0), (137.5, 42.25), (400.0, 400.0) })
        {
            var back = h.View.ToMillimetres(h.View.ToPixels(x, y));
            Assert.Equal(x, back.X, 6);
            Assert.Equal(y, back.Y, 6);
        }
    }

    [AvaloniaFact]
    public void FitToBedCentresTheBedInTheVisibleArea()
    {
        var h = new Harness();
        h.View.ChromeInset = new Thickness(0);
        h.View.ZoomToFitBed();

        var centre = h.View.ToPixels(200, 200);
        Assert.Equal(400, centre.X, 0);
        Assert.Equal(300, centre.Y, 0);
    }
}
