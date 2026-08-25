using Avalonia.Headless.XUnit;
using Emberline.AI;
using Emberline.Core.Documents;
using Xunit;

namespace Emberline.App.Tests;

/// <summary>
/// The assistant drawing artwork.
///
/// This is a document change, not a machine action: nothing moves and nothing
/// burns, so it does not go through the confirmation gate. What has to hold is
/// that it lands on the canvas correctly, that it is undoable, and that bad input
/// is refused with something the model can act on rather than accepted quietly.
/// </summary>
public class AssistantDrawingTests : ShellTest
{
    private const string Square =
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="40mm" height="40mm" viewBox="0 0 40 40">
          <rect x="0" y="0" width="40" height="40" fill="none" stroke="black"/>
        </svg>
        """;

    [AvaloniaFact]
    public void DrawnArtworkLandsOnTheCanvas()
    {
        var shell = NewShell();

        var reply = shell.AddArtwork(Square, "Test square");

        var shape = Assert.IsType<PathShape>(Assert.Single(shell.Design.Shapes));
        Assert.Equal("Test square", shape.Name);
        Assert.Contains("40", reply, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ItArrivesAtTheSizeTheSvgAsksFor()
    {
        var shell = NewShell();
        shell.AddArtwork(Square, "Square");

        var bounds = shell.Design.Shapes[0].Bounds;

        Assert.Equal(40, bounds.Width, 1);
        Assert.Equal(40, bounds.Height, 1);
    }

    [AvaloniaFact]
    public void ItIsCentredOnTheBedWhenThereIsNoWorkpiece()
    {
        var shell = NewShell();
        shell.AddArtwork(Square, "Square");

        var centre = shell.Design.Shapes[0].Bounds.Center;

        Assert.Equal(shell.SelectedMachine.BedWidthMm / 2, centre.X, 1);
        Assert.Equal(shell.SelectedMachine.BedHeightMm / 2, centre.Y, 1);
    }

    [AvaloniaFact]
    public void ItIsCentredOnTheWorkpieceWhenThereIsOne()
    {
        // The workpiece is what is actually being burned onto.
        var shell = NewShell();
        shell.SelectedWorkpiecePreset = shell.WorkpiecePresets.First(p => p.Name.StartsWith("100 mm square"));
        shell.WorkpieceXMm = 25;
        shell.WorkpieceYMm = 35;

        shell.AddArtwork(Square, "Square");

        var centre = shell.Design.Shapes[0].Bounds.Center;
        Assert.Equal(75, centre.X, 1);
        Assert.Equal(85, centre.Y, 1);
    }

    [AvaloniaFact]
    public void DrawingIsOneUndoStep()
    {
        var shell = NewShell();
        shell.AddArtwork(Square, "Square");
        Assert.Single(shell.Design.Shapes);

        shell.UndoEditCommand.Execute(null);

        Assert.Empty(shell.Design.Shapes);
    }

    [AvaloniaFact]
    public void NonsenseIsRefusedWithSomethingTheModelCanActOn()
    {
        var shell = NewShell();

        var reply = shell.AddArtwork("this is not svg at all", "Nope");

        Assert.Empty(shell.Design.Shapes);
        Assert.NotEmpty(reply);
    }

    [AvaloniaFact]
    public void AnSvgWithNoStrokedPathsSaysWhyRatherThanAddingNothing()
    {
        var shell = NewShell();

        var reply = shell.AddArtwork(
            """<svg xmlns="http://www.w3.org/2000/svg" width="10mm" height="10mm" viewBox="0 0 10 10"></svg>""",
            "Empty");

        Assert.Empty(shell.Design.Shapes);
        Assert.Contains("outline", reply, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void AnAbsurdlyLargeDocumentIsRefusedBeforeItIsParsed()
    {
        var shell = NewShell();

        var huge = "<svg>" + new string('x', 600 * 1024) + "</svg>";
        var reply = shell.AddArtwork(huge, "Huge");

        Assert.Empty(shell.Design.Shapes);
        Assert.Contains("limit", reply, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void ArtworkFarLargerThanTheBedIsRefused()
    {
        // Almost always a units mistake rather than an intention.
        var shell = NewShell();

        const string enormous =
            """
            <svg xmlns="http://www.w3.org/2000/svg" width="9000mm" height="9000mm" viewBox="0 0 9000 9000">
              <rect x="0" y="0" width="9000" height="9000" fill="none" stroke="black"/>
            </svg>
            """;

        var reply = shell.AddArtwork(enormous, "Enormous");

        Assert.Empty(shell.Design.Shapes);
        Assert.Contains("units", reply, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void AnExternalEntityIsNeverFetched()
    {
        // The SVG now arrives from a model, so the XML parser's appetite matters.
        // XDocument.Parse reads the DOCTYPE but does not resolve external
        // entities: the drawing still imports, and the file never does. This pins
        // that, because the day it changes nothing else would notice.
        var shell = NewShell();

        var probe = Path.Combine(Path.GetTempPath(), $"emberline-xxe-{Guid.NewGuid():N}.txt");
        const string secret = "SECRET-THAT-MUST-NOT-ESCAPE";
        File.WriteAllText(probe, secret);

        try
        {
            var xxe = $"""
                <?xml version="1.0"?>
                <!DOCTYPE svg [<!ENTITY leak SYSTEM "file://{probe}">]>
                <svg xmlns="http://www.w3.org/2000/svg" width="10mm" height="10mm" viewBox="0 0 10 10">
                  <path d="M0,0 L10,10" fill="none" stroke="black"/>
                  <desc>&leak;</desc>
                </svg>
                """;

            var reply = shell.AddArtwork(xxe, "Sneaky");

            // The legitimate path is imported — the document is not rejected.
            Assert.Single(shell.Design.Shapes);

            // And nothing from the file reaches the reply, the shape or the console.
            Assert.DoesNotContain(secret, reply, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, shell.Design.Shapes[0].Name, StringComparison.Ordinal);
            Assert.DoesNotContain(shell.Console.Lines, l => l.Text.Contains(secret, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(probe);
        }
    }

    [AvaloniaFact]
    public void TheToolIsOfferedToTheModel()
    {
        Assert.Contains(AssistantTools.All, t => t.Name == AssistantTools.DrawSvg);
        var tool = AssistantTools.All.First(t => t.Name == AssistantTools.DrawSvg);

        // The description has to teach the difference between screen SVG and
        // laser SVG, or the model sends filled artwork every time.
        Assert.Contains("fill=\"none\"", tool.Description, StringComparison.Ordinal);
        Assert.Contains("millimetres", tool.Description, StringComparison.OrdinalIgnoreCase);
    }
}
