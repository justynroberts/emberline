using Avalonia.Headless.XUnit;
using OpenBurn.App.ViewModels;
using OpenBurn.App.Views;
using OpenBurn.Cam.Trace;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;
using OpenBurn.Core.Storage;
using Xunit;

namespace OpenBurn.App.Tests;

/// <summary>
/// The trace dialog, driven through the real view model.
///
/// The engine's own behaviour is covered in the CAM tests; what is worth checking
/// here is the part a user actually experiences — that changing a setting changes
/// the result, that the preview keeps up, and that what lands on the bed sits
/// exactly where the image it came from was sitting.
/// </summary>
public class TraceTests : ShellTest
{
    /// <summary>A disc on white — something with an unambiguous outline.</summary>
    private static RasterImage Disc(int size = 80, byte ink = 0)
    {
        var px = new byte[size * size];
        Array.Fill(px, (byte)255);
        var r = size * 0.35;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - size / 2.0;
                var dy = y - size / 2.0;
                if (Math.Sqrt(dx * dx + dy * dy) < r) px[y * size + x] = ink;
            }
        }
        return new RasterImage(size, size, px);
    }

    /// <summary>A dark ring — outline mode gives two contours, centreline one.</summary>
    private static RasterImage Ring(int size = 90)
    {
        var px = new byte[size * size];
        Array.Fill(px, (byte)255);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var d = Math.Sqrt((x - size / 2.0) * (x - size / 2.0) + (y - size / 2.0) * (y - size / 2.0));
                if (d > size * 0.28 && d < size * 0.36) px[y * size + x] = 0;
            }
        }
        return new RasterImage(size, size, px);
    }

    private static TraceViewModel Editor(RasterImage image, double widthMm = 60, double heightMm = 60) =>
        new(image, "test image", widthMm, heightMm, useTimer: false);

    private MainViewModel CreateShell()
    {
        return NewShell();
    }

    // ------------------------------------------------------------- the editor

    [AvaloniaFact]
    public void OpeningTheEditorAlreadyShowsATrace()
    {
        var editor = Editor(Disc());

        Assert.NotEmpty(editor.Contours);
        Assert.NotNull(editor.Preview);
        Assert.Contains("outline", editor.Summary);
        Assert.False(editor.HasNote);
    }

    [AvaloniaFact]
    public void TheThresholdStartsWhereTheImageActuallySplits()
    {
        // Faint grey artwork on a bright ground: a fixed 128 would find nothing.
        var px = new byte[60 * 60];
        Array.Fill(px, (byte)235);
        for (var y = 15; y < 45; y++)
        {
            for (var x = 15; x < 45; x++) px[y * 60 + x] = 175;
        }

        var editor = Editor(new RasterImage(60, 60, px));

        Assert.InRange(editor.Threshold, 176, 235);
        Assert.NotEmpty(editor.Contours);
    }

    [AvaloniaFact]
    public void MovingTheThresholdChangesWhatIsFound()
    {
        // A mid-grey disc, so there is a threshold either side of it.
        var editor = Editor(Disc(ink: 160));
        Assert.NotEmpty(editor.Contours);

        editor.Threshold = 100;   // now nothing in the image is dark enough

        Assert.Empty(editor.Contours);
        Assert.Contains("Nothing found", editor.Summary);

        editor.Threshold = 200;   // and now the disc is back

        Assert.NotEmpty(editor.Contours);
    }

    [AvaloniaFact]
    public void CentrelineModeGivesOneStrokeWhereOutlineModeGivesTwoEdges()
    {
        var editor = Editor(Ring());

        Assert.Equal(2, editor.Contours.Count);          // outside and inside of the ring
        Assert.All(editor.Contours, c => Assert.True(c.IsClosed));

        editor.Centreline = true;

        Assert.Single(editor.Contours);                  // one line round the middle
        Assert.Equal(TraceMode.Centreline, editor.Mode);
        Assert.Contains("stroke", editor.Summary);
    }

    [AvaloniaFact]
    public void InvertingTracesTheBackgroundInstead()
    {
        var editor = Editor(Disc());
        var before = editor.Contours[0].Bounds;

        editor.Invert = true;

        Assert.NotEmpty(editor.Contours);
        // The light region is everything outside the disc, so its bounds are the
        // whole image rather than the disc.
        Assert.True(editor.Contours[0].Bounds.Width > before.Width);
    }

    [AvaloniaFact]
    public void ResetPutsEverythingBack()
    {
        var editor = Editor(Disc());

        editor.Centreline = true;
        editor.Invert = true;
        editor.Simplify = 3;
        editor.SmoothPasses = 0;

        editor.ResetCommand.Execute(null);

        Assert.False(editor.Centreline);
        Assert.False(editor.Invert);
        Assert.Equal(0.8, editor.Simplify);
        Assert.Equal(2, editor.SmoothPasses);
        Assert.True(editor.Despeckle);
    }

    [AvaloniaFact]
    public void ThePreviewIsRedrawnWheneverASettingChanges()
    {
        var editor = Editor(Disc());
        var first = editor.Preview;

        editor.ShowSource = false;

        Assert.NotNull(editor.Preview);
        Assert.NotSame(first, editor.Preview);
    }

    [AvaloniaFact]
    public void TheShapeComesOutAtTheRequestedSize()
    {
        var editor = Editor(Disc(), widthMm: 40, heightMm: 40);

        var shape = editor.BuildShape();

        Assert.NotNull(shape);
        var bounds = shape!.LocalBounds;
        // The disc is 70% of the image, so 28 mm of a 40 mm placement.
        Assert.InRange(bounds.Width, 24, 32);
        Assert.InRange(bounds.Height, 24, 32);
    }

    [AvaloniaFact]
    public void NothingFoundMeansNothingToAdd()
    {
        var editor = Editor(Disc(ink: 160));
        editor.Threshold = 100;

        Assert.Null(editor.BuildShape());
    }

    // -------------------------------------------------------------- the shell

    [AvaloniaFact]
    public void TheEditorIsOnlyOfferedForAnImage()
    {
        var shell = CreateShell();

        Assert.Null(shell.CreateTraceEditor());

        shell.Design.AddShape(new PathShape([new Polyline([new Vec2(0, 0), new Vec2(10, 10)])]), shell.Design.Layers[0]);
        shell.Selection.Add(shell.Design.Shapes[0]);

        Assert.Null(shell.CreateTraceEditor());
    }

    [AvaloniaFact]
    public void TracingAPlacedImageLandsExactlyOnTopOfIt()
    {
        var shell = CreateShell();

        var image = new ImageShape(Disc(), 50, 50) { Name = "disc" };
        image.Translate(new Vec2(30, 40));
        shell.Design.AddShape(image, shell.Design.Layers[0]);
        shell.Selection.Add(image);

        var editor = shell.CreateTraceEditor();
        Assert.NotNull(editor);

        var traced = editor!.BuildShape();
        Assert.NotNull(traced);

        shell.ApplyTrace(editor, traced!);

        var added = Assert.IsType<PathShape>(shell.Design.Shapes[^1]);
        var imageBounds = image.Bounds;
        var tracedBounds = added.Bounds;

        // Same place on the bed, within the disc's own margin inside the image.
        Assert.InRange(tracedBounds.Center.X, imageBounds.Center.X - 2, imageBounds.Center.X + 2);
        Assert.InRange(tracedBounds.Center.Y, imageBounds.Center.Y - 2, imageBounds.Center.Y + 2);
    }

    [AvaloniaFact]
    public void TracingIsOneUndoStepAndLeavesTheImageAlone()
    {
        var shell = CreateShell();

        var image = new ImageShape(Disc(), 50, 50) { Name = "disc" };
        shell.Design.AddShape(image, shell.Design.Layers[0]);
        shell.Selection.Add(image);

        var editor = shell.CreateTraceEditor()!;
        shell.ApplyTrace(editor, editor.BuildShape()!);

        Assert.Equal(2, shell.Design.Shapes.Count);

        shell.UndoEditCommand.Execute(null);

        var remaining = Assert.Single(shell.Design.Shapes);
        Assert.Same(image, remaining);
    }

    [AvaloniaFact]
    public void TheWindowReturnsTheShapeOnlyWhenAccepted()
    {
        var editor = Editor(Disc());

        // Constructing it parses the XAML, so a broken binding fails here rather
        // than at runtime. Closing it again matters: a headless window left open
        // tears down its compositor on another thread later, and the failure lands
        // on whichever unrelated test ran last.
        var window = new TraceWindow(editor);
        try
        {
            // Nothing has been accepted yet.
            Assert.Null(window.Result);
        }
        finally
        {
            window.Close();
        }
    }
}
