using Avalonia.Headless.XUnit;
using Emberline.App.ViewModels;
using Emberline.Core.Documents;
using Xunit;

namespace Emberline.App.Tests;

/// <summary>
/// Bringing catalogue artwork onto the bed.
///
/// Icons are drawn as filled regions, which is the opposite of how a laser
/// thinks. Both readings are useful — darken the shape, or cut it out — so the
/// import says which, and the difference is carried by the layer rather than by
/// the geometry.
/// </summary>
public class CatalogImportTests : ShellTest
{
    private const string Icon =
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="24mm" height="24mm" viewBox="0 0 24 24">
          <path d="M3 3 L21 3 L21 21 L3 21 Z"/>
        </svg>
        """;

    [AvaloniaFact]
    public void EtchingPutsItOnALayerThatFillsTheShape()
    {
        var shell = NewShell();

        shell.AddCatalogArtwork(Icon, "Fox", CatalogImportMode.Etch);

        var shape = Assert.Single(shell.Design.Shapes);
        var layer = shell.Design.FindLayer(shape.LayerId);

        Assert.NotNull(layer);
        Assert.Equal(OperationKind.Fill, layer!.Operation);
    }

    [AvaloniaFact]
    public void CuttingPutsItOnALayerThatFollowsTheOutline()
    {
        var shell = NewShell();

        shell.AddCatalogArtwork(Icon, "Fox", CatalogImportMode.Cut);

        var shape = Assert.Single(shell.Design.Shapes);
        var layer = shell.Design.FindLayer(shape.LayerId);

        Assert.NotNull(layer);
        Assert.Equal(OperationKind.Cut, layer!.Operation);
    }

    [AvaloniaFact]
    public void TheGeometryIsTheSameEitherWay()
    {
        // Which means the choice can be changed afterwards by moving the shape to
        // another layer, rather than importing it again.
        var etched = NewShell();
        var cut = NewShell();

        etched.AddCatalogArtwork(Icon, "A", CatalogImportMode.Etch);
        cut.AddCatalogArtwork(Icon, "A", CatalogImportMode.Cut);

        var a = etched.Design.Shapes[0].Bounds;
        var b = cut.Design.Shapes[0].Bounds;

        Assert.Equal(a.Width, b.Width, 3);
        Assert.Equal(a.Height, b.Height, 3);
    }

    [AvaloniaFact]
    public void ImportingSeveralReusesOneLayerRatherThanMakingOneEach()
    {
        // Six icons to etch should be one layer with six shapes, not six layers
        // that each need the same speed and power set on them.
        var shell = NewShell();
        var before = shell.Design.Layers.Count;

        for (var i = 0; i < 4; i++) shell.AddCatalogArtwork(Icon, $"Icon {i}", CatalogImportMode.Etch);

        Assert.Equal(4, shell.Design.Shapes.Count);
        Assert.Equal(1, shell.Design.Layers.Count(l => l.Operation == OperationKind.Fill));
        Assert.InRange(shell.Design.Layers.Count, before, before + 1);
    }

    [AvaloniaFact]
    public void ItArrivesAtTheSizeAskedFor()
    {
        var shell = NewShell();

        shell.AddCatalogArtwork(Icon, "Fox", CatalogImportMode.Cut, sizeMm: 80);

        var bounds = shell.Design.Shapes[0].Bounds;
        Assert.Equal(80, Math.Max(bounds.Width, bounds.Height), 1);
    }

    [AvaloniaFact]
    public void ItLandsOnTheWorkpieceWhenThereIsOne()
    {
        var shell = NewShell();
        shell.SelectedWorkpiecePreset = shell.WorkpiecePresets.First(p => p.Name.StartsWith("100 mm square"));
        shell.WorkpieceXMm = 40;
        shell.WorkpieceYMm = 60;

        shell.AddCatalogArtwork(Icon, "Fox", CatalogImportMode.Etch, sizeMm: 40);

        var centre = shell.Design.Shapes[0].Bounds.Center;
        Assert.Equal(90, centre.X, 1);
        Assert.Equal(110, centre.Y, 1);
    }

    [AvaloniaFact]
    public void ImportingIsOneUndoStep()
    {
        var shell = NewShell();
        shell.AddCatalogArtwork(Icon, "Fox", CatalogImportMode.Etch);

        shell.UndoEditCommand.Execute(null);

        Assert.Empty(shell.Design.Shapes);
    }

    [AvaloniaFact]
    public void RubbishIsRefusedWithAReason()
    {
        var shell = NewShell();

        Assert.Contains("no outlines", shell.AddCatalogArtwork(
            """<svg xmlns="http://www.w3.org/2000/svg" width="10mm" height="10mm" viewBox="0 0 10 10"></svg>""",
            "Empty", CatalogImportMode.Cut), StringComparison.OrdinalIgnoreCase);

        Assert.Empty(shell.Design.Shapes);
    }
}

/// <summary>
/// Text has to make the same choice the artwork import does.
///
/// Letterforms are closed regions, so whether they are darkened or cut round is a
/// real decision — not something to be settled by whichever layer happened to be
/// selected. An outlined "A" burned where a filled one was meant looks like a
/// mistake; a filled one where an outline was meant takes twenty times as long.
/// </summary>
public class TextFillTests : ShellTest
{
    [AvaloniaFact]
    public void FilledTextGoesOnALayerThatFillsIt()
    {
        var shell = NewShell();
        shell.TextInput = "Hello";
        shell.TextFilled = true;

        shell.AddTextCommand.Execute(null);

        var shape = Assert.Single(shell.Design.Shapes);
        var layer = shell.Design.FindLayer(shape.LayerId);
        Assert.Equal(OperationKind.Fill, layer!.Operation);
    }

    [AvaloniaFact]
    public void OutlineTextStaysOnTheSelectedLayer()
    {
        var shell = NewShell();
        var engrave = shell.Design.Layers.First(l => l.Operation == OperationKind.Engrave);
        shell.SelectedLayer = shell.Layers.First(l => l.Layer == engrave);

        shell.TextInput = "Hello";
        shell.TextFilled = false;
        shell.AddTextCommand.Execute(null);

        var shape = Assert.Single(shell.Design.Shapes);
        Assert.Equal(engrave.Id, shape.LayerId);
    }

    [AvaloniaFact]
    public void FilledIsTheDefaultBecauseThatIsWhatEngravedLetteringMeans()
    {
        Assert.True(NewShell().TextFilled);
    }

    [AvaloniaFact]
    public void SeveralPiecesOfFilledTextShareOneLayer()
    {
        var shell = NewShell();
        shell.TextFilled = true;

        foreach (var word in new[] { "one", "two", "three" })
        {
            shell.TextInput = word;
            shell.AddTextCommand.Execute(null);
        }

        Assert.Equal(3, shell.Design.Shapes.Count);
        Assert.Equal(1, shell.Design.Layers.Count(l => l.Operation == OperationKind.Fill));
    }
}
