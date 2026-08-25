using Avalonia;
using Avalonia.Headless.XUnit;
using Emberline.App.Controls;
using Emberline.Cam.Trace;
using Emberline.Core.Documents;
using Emberline.Core.Geometry;
using Emberline.Core.Machines;
using Xunit;

namespace Emberline.App.Tests;

/// <summary>
/// The canvas caches the shape geometry between frames.
///
/// It used to rebuild every frame, on the grounds that a document is small next to
/// a toolpath. That holds for a few imported curves and breaks completely for a
/// traced bitmap — a quarter of a million points rebuilt on every frame, several
/// times over once selection and hit-testing have had their turn.
/// </summary>
public class GeometryCacheTests
{
    /// <summary>A traced bitmap: the case that made this necessary.</summary>
    private static PathShape TracedShape()
    {
        var rng = new Random(11);
        var w = 500;
        var h = 380;
        var px = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = 128 + 110 * Math.Sin(x / 9.0) * Math.Cos(y / 11.0) + rng.Next(-30, 30);
                px[y * w + x] = (byte)Math.Clamp(v, 0, 255);
            }
        }

        var image = new RasterImage(w, h, px);
        var traced = BitmapTracer.Trace(image);
        return BitmapTracer.ToShape(traced, w, h, 150, 114);
    }

    private static WorkspaceView Canvas(out Design design)
    {
        design = Design.CreateDefault();
        design.AddShape(TracedShape(), design.Layers[0]);

        return new WorkspaceView
        {
            Machine = MachineLibrary.Load().Default,
            Design = design,
            Width = 800,
            Height = 600,
        };
    }

    /// <summary>
    /// One frame's worth of geometry work — the same call DrawShapes makes.
    ///
    /// Deliberately not a real render pass: driving Render in a headless test
    /// leaves rendering infrastructure to be disposed later on another thread,
    /// and the resulting cleanup failure is reported against whichever unrelated
    /// test happened to run last. What is under test here is the cache, not
    /// Avalonia's rasteriser.
    /// </summary>
    private static void Render(WorkspaceView view) => view.EnsureShapeGeometry();

    [AvaloniaFact]
    public void TheTracedShapeIsBigEnoughForThisToMatter()
    {
        var view = Canvas(out var design);
        var points = ((PathShape)design.Shapes[0]).Paths.Sum(p => p.Count);

        Assert.True(points > 10_000, $"only {points:N0} points — the fixture is too small to be a fair test");
    }

    [AvaloniaFact]
    public void RedrawingWithoutChangesDoesNotRebuildTheGeometry()
    {
        var view = Canvas(out _);

        Render(view);
        Assert.Equal(1, view.GeometryRebuilds);

        for (var i = 0; i < 20; i++) Render(view);

        Assert.Equal(1, view.GeometryRebuilds);
    }

    [AvaloniaFact]
    public void PanningAndZoomingDoNotRebuildIt()
    {
        // The transform is applied at draw time, so the geometry itself does not
        // depend on where the view happens to be looking.
        var view = Canvas(out _);
        Render(view);

        view.ZoomToFitContent();
        Render(view);
        view.ZoomToFitBed();
        Render(view);

        Assert.Equal(1, view.GeometryRebuilds);
    }

    [AvaloniaFact]
    public void ChangingTheDocumentDoesRebuildIt()
    {
        var view = Canvas(out var design);
        Render(view);
        Assert.Equal(1, view.GeometryRebuilds);

        design.AddShape(PathShape.Rectangle(20, 20), design.Layers[0]);
        view.DocumentVersion++;
        Render(view);

        Assert.Equal(2, view.GeometryRebuilds);
    }

    [AvaloniaFact]
    public void AnEditOnTheCanvasItselfRebuildsIt()
    {
        // Dragging mutates shapes in place, which no property change would catch.
        var view = Canvas(out _);
        Render(view);

        view.InvalidateShapes();
        Render(view);

        Assert.Equal(2, view.GeometryRebuilds);
    }

    [AvaloniaFact]
    public void ADifferentDocumentRebuildsItEvenAtTheSameVersion()
    {
        var view = Canvas(out _);
        Render(view);

        var replacement = Design.CreateDefault();
        replacement.AddShape(PathShape.Rectangle(30, 30), replacement.Layers[0]);
        view.Design = replacement;
        Render(view);

        Assert.Equal(2, view.GeometryRebuilds);
    }
}
