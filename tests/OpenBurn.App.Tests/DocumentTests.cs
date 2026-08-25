using Avalonia.Headless.XUnit;
using OpenBurn.App.ViewModels;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;
using OpenBurn.Core.Storage;
using Xunit;

namespace OpenBurn.App.Tests;

/// <summary>
/// Saving and reopening from the shell.
///
/// Until this existed a design lasted exactly as long as the window stayed open.
/// Exported G-code is the output, not the document — every decision is already
/// baked out of it and there is no way back.
/// </summary>
public class DocumentTests : ShellTest
{
    private static string Scratch(string name) =>
        Path.Combine(Path.GetTempPath(), $"openburn-doc-{Guid.NewGuid():N}-{name}{DesignFile.Extension}");

    private static PathShape Square(double x, double size) => new([new Polyline(
    [
        new Vec2(x, 0), new Vec2(x + size, 0), new Vec2(x + size, size), new Vec2(x, size),
    ], closed: true)]);

    [AvaloniaFact]
    public void ANewDocumentHasNothingToSaveYet()
    {
        var shell = NewShell();
        Assert.False(shell.HasUnsavedChanges);
        Assert.DoesNotContain("•", shell.DocumentTitle, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ChangingSomethingMarksTheDocument()
    {
        // Otherwise the only way to know whether work is safe is to remember.
        var shell = NewShell();
        shell.Design.AddShape(Square(10, 20), shell.Design.Layers[0]);
        shell.QueueRegenerate();

        Assert.True(shell.HasUnsavedChanges);
        Assert.EndsWith("•", shell.DocumentTitle, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void SavingAndReopeningBringsTheWorkBack()
    {
        var path = Scratch("round-trip");
        try
        {
            var first = NewShell();
            first.Design.Name = "Coaster";
            first.Design.AddShape(Square(30, 25), first.Design.Layers[0]);
            first.SelectedWorkpiecePreset = first.WorkpiecePresets.First(p => p.Name.StartsWith("100 mm square"));
            DesignFile.Save(first.Design, path);

            var second = NewShell();
            second.OpenDesign(path);

            Assert.Single(second.Design.Shapes);
            Assert.True(second.Design.Workpiece.IsSet);
            Assert.Equal("Coaster", second.Design.Name);
            Assert.False(second.HasUnsavedChanges);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void OpeningReplacesWhatWasOnTheBed()
    {
        var path = Scratch("replace");
        try
        {
            var source = NewShell();
            source.Design.AddShape(Square(0, 10), source.Design.Layers[0]);
            DesignFile.Save(source.Design, path);

            var shell = NewShell();
            shell.Design.AddShape(Square(0, 10), shell.Design.Layers[0]);
            shell.Design.AddShape(Square(50, 10), shell.Design.Layers[0]);
            Assert.Equal(2, shell.Design.Shapes.Count);

            shell.OpenDesign(path);

            Assert.Single(shell.Design.Shapes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void OpeningADesignThroughTheOrdinaryImportPathStillOpensIt()
    {
        // Dropping a .openburn file on the window must open it, not try to trace it.
        var path = Scratch("dropped");
        try
        {
            var source = NewShell();
            source.Design.Name = "Dropped";
            source.Design.AddShape(Square(5, 15), source.Design.Layers[0]);
            DesignFile.Save(source.Design, path);

            var shell = NewShell();
            shell.ImportFile(path);

            Assert.Equal("Dropped", shell.Design.Name);
            Assert.Single(shell.Design.Shapes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void ReopeningLeavesNothingToUndoFromBefore()
    {
        // Undoing past the point a file was opened would restore a document the
        // operator never saw.
        var path = Scratch("undo");
        try
        {
            var source = NewShell();
            source.Design.AddShape(Square(0, 10), source.Design.Layers[0]);
            DesignFile.Save(source.Design, path);

            var shell = NewShell();
            shell.Design.AddShape(Square(80, 10), shell.Design.Layers[0]);
            shell.QueueRegenerate();

            shell.OpenDesign(path);

            Assert.False(shell.CanUndo);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void AFileThatIsNotADesignIsRefusedWithoutLosingTheCurrentWork()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openburn-bad-{Guid.NewGuid():N}{DesignFile.Extension}");
        File.WriteAllText(path, "certainly not a design");

        try
        {
            var shell = NewShell();
            shell.Design.AddShape(Square(0, 10), shell.Design.Layers[0]);

            shell.OpenDesign(path);

            // The work on the bed must survive a failed open.
            Assert.Single(shell.Design.Shapes);
            Assert.Contains(shell.Console.Lines, l => l.Text.Contains("Could not open", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void TheTitleNamesTheFileOnceThereIsOne()
    {
        var path = Scratch("named");
        try
        {
            var shell = NewShell();
            DesignFile.Save(shell.Design, path);
            shell.OpenDesign(path);

            Assert.Contains(Path.GetFileNameWithoutExtension(path), shell.DocumentTitle, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// Transforming a selection by number rather than by drag.
///
/// The handles do freeform: eight for scale, one for rotation, Shift to snap the
/// angle or hold the aspect. What they cannot do is "twelve degrees", which is a
/// thing a drawing tells you.
/// </summary>
public class TransformTests : ShellTest
{
    private static PathShape Square(double size) => new([new Polyline(
    [
        new Vec2(0, 0), new Vec2(size, 0), new Vec2(size, size), new Vec2(0, size),
    ], closed: true)]);

    private MainViewModel WithSquare(out PathShape shape)
    {
        var shell = NewShell();
        shape = Square(40);
        shell.Design.AddShape(shape, shell.Design.Layers[0]);
        shell.SetSelection([shape], additive: false);
        return shell;
    }

    [AvaloniaFact]
    public void AnUnrotatedShapeReadsAsZero()
    {
        var shell = WithSquare(out _);
        Assert.Equal(0, shell.SelectedRotationDeg, 3);
    }

    [AvaloniaFact]
    public void TypingAnAngleSetsItRatherThanAddingToIt()
    {
        // A number in a box is where the thing ends up, not how far to turn it.
        var shell = WithSquare(out _);

        shell.SelectedRotationDeg = 30;
        Assert.Equal(30, shell.SelectedRotationDeg, 2);

        shell.SelectedRotationDeg = 30;
        Assert.Equal(30, shell.SelectedRotationDeg, 2);

        shell.SelectedRotationDeg = 45;
        Assert.Equal(45, shell.SelectedRotationDeg, 2);
    }

    [AvaloniaFact]
    public void ZeroPutsItBackSquare()
    {
        var shell = WithSquare(out var shape);
        var before = shape.Bounds;

        shell.SelectedRotationDeg = 37;
        shell.SelectedRotationDeg = 0;

        Assert.Equal(0, shell.SelectedRotationDeg, 2);
        Assert.Equal(before.Width, shape.Bounds.Width, 2);
        Assert.Equal(before.Height, shape.Bounds.Height, 2);
    }

    [AvaloniaFact]
    public void RotatingIsUndoable()
    {
        var shell = WithSquare(out _);
        shell.SelectedRotationDeg = 25;

        shell.UndoEditCommand.Execute(null);

        Assert.Equal(0, shell.SelectedRotationDeg, 2);
    }

    [AvaloniaFact]
    public void AnglesWrapRatherThanGrowing()
    {
        // -170 and 190 are the same place, and one of them reads badly.
        var shell = WithSquare(out _);

        shell.SelectedRotationDeg = 190;

        Assert.InRange(shell.SelectedRotationDeg, -180, 180);
        Assert.Equal(-170, shell.SelectedRotationDeg, 2);
    }

    [AvaloniaFact]
    public void RotatingTurnsAboutTheMiddleOfTheSelection()
    {
        var shell = WithSquare(out var shape);
        var centre = shape.Bounds.Center;

        shell.SelectedRotationDeg = 90;

        Assert.Equal(centre.X, shape.Bounds.Center.X, 2);
        Assert.Equal(centre.Y, shape.Bounds.Center.Y, 2);
    }
}
