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
