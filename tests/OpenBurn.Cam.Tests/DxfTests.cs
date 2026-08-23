using System.Text;
using OpenBurn.Cam.Import;
using OpenBurn.Core.Geometry;
using Xunit;

namespace OpenBurn.Cam.Tests;

public class DxfImporterTests
{
    /// <summary>Build a minimal but valid ASCII DXF from group-code pairs.</summary>
    private static string Dxf(string entities, string? header = null, string? blocks = null)
    {
        var sb = new StringBuilder();

        if (header is not null)
        {
            sb.Append("0\nSECTION\n2\nHEADER\n").Append(header).Append("0\nENDSEC\n");
        }

        if (blocks is not null)
        {
            sb.Append("0\nSECTION\n2\nBLOCKS\n").Append(blocks).Append("0\nENDSEC\n");
        }

        sb.Append("0\nSECTION\n2\nENTITIES\n").Append(entities).Append("0\nENDSEC\n0\nEOF\n");
        return sb.ToString();
    }

    private static string Line(double x1, double y1, double x2, double y2) =>
        $"0\nLINE\n8\n0\n10\n{x1}\n20\n{y1}\n11\n{x2}\n21\n{y2}\n";

    [Fact]
    public void ImportsASingleLine()
    {
        var result = DxfImporter.Parse(Dxf(Line(0, 0, 100, 0)));

        var path = Assert.Single(result.Paths);
        Assert.Equal(2, path.Count);
        Assert.Equal(100, path[1].X, 6);
        Assert.Equal(100, result.WidthMm, 6);
    }

    [Fact]
    public void ImportsACircleAtTheRightSize()
    {
        var result = DxfImporter.Parse(Dxf("0\nCIRCLE\n10\n50\n20\n50\n40\n25\n"));

        var circle = Assert.Single(result.Paths);
        Assert.True(circle.IsClosed);
        Assert.Equal(50, circle.Bounds.Width, 1);
        Assert.Equal(157.08, circle.Length, 0);
    }

    [Fact]
    public void ImportsAnArcCounterClockwise()
    {
        // A quarter arc of radius 10, from 0° to 90°, is 15.708 mm long.
        var result = DxfImporter.Parse(Dxf("0\nARC\n10\n0\n20\n0\n40\n10\n50\n0\n51\n90\n"));

        var arc = Assert.Single(result.Paths);
        Assert.Equal(15.708, arc.Length, 1);
        Assert.Equal(10, arc.First.X, 3);
        Assert.Equal(0, arc.First.Y, 3);
        Assert.Equal(0, arc.Last.X, 2);
        Assert.Equal(10, arc.Last.Y, 2);
    }

    [Fact]
    public void ImportsALightweightPolyline()
    {
        var entity = "0\nLWPOLYLINE\n90\n4\n70\n1\n" +
                     "10\n0\n20\n0\n10\n40\n20\n0\n10\n40\n20\n30\n10\n0\n20\n30\n";

        var result = DxfImporter.Parse(Dxf(entity));

        var poly = Assert.Single(result.Paths);
        Assert.True(poly.IsClosed);
        Assert.Equal(4, poly.Count);
        Assert.Equal(140, poly.Length, 3);
    }

    [Fact]
    public void PolylineBulgesBecomeArcs()
    {
        // A bulge of 1 is a half circle. Two vertices 20 mm apart with bulge 1 give
        // a semicircle of radius 10, so 31.4 mm rather than a 20 mm chord.
        var straight = "0\nLWPOLYLINE\n90\n2\n70\n0\n10\n0\n20\n0\n10\n20\n20\n0\n";
        var bulged = "0\nLWPOLYLINE\n90\n2\n70\n0\n10\n0\n20\n0\n42\n1\n10\n20\n20\n0\n";

        var straightLength = DxfImporter.Parse(Dxf(straight)).Paths[0].Length;
        var bulgedLength = DxfImporter.Parse(Dxf(bulged)).Paths[0].Length;

        Assert.Equal(20, straightLength, 3);
        Assert.Equal(31.4, bulgedLength, 0);
    }

    [Fact]
    public void ImportsOldStylePolylineWithVertexEntities()
    {
        // The form plenty of CAD packages still emit; reading entities
        // independently drops these entirely.
        var entity = "0\nPOLYLINE\n70\n1\n" +
                     "0\nVERTEX\n10\n0\n20\n0\n" +
                     "0\nVERTEX\n10\n30\n20\n0\n" +
                     "0\nVERTEX\n10\n30\n20\n40\n" +
                     "0\nSEQEND\n";

        var result = DxfImporter.Parse(Dxf(entity));

        var poly = Assert.Single(result.Paths);
        Assert.True(poly.IsClosed);
        Assert.Equal(3, poly.Count);
    }

    [Fact]
    public void ImportsAnEllipse()
    {
        // Centre at origin, major axis 40 along X, ratio 0.5, so 40 × 20.
        var entity = "0\nELLIPSE\n10\n0\n20\n0\n11\n20\n21\n0\n40\n0.5\n41\n0\n42\n6.283185307\n";

        var ellipse = Assert.Single(DxfImporter.Parse(Dxf(entity)).Paths);

        Assert.Equal(40, ellipse.Bounds.Width, 1);
        Assert.Equal(20, ellipse.Bounds.Height, 1);
    }

    [Fact]
    public void InchDrawingsAreConvertedToMillimetres()
    {
        var header = "9\n$INSUNITS\n70\n1\n";
        var result = DxfImporter.Parse(Dxf(Line(0, 0, 1, 0), header));

        Assert.Equal(25.4, result.WidthMm, 6);
        Assert.Contains("inches", result.Units, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MillimetreDrawingsAreLeftAlone()
    {
        var header = "9\n$INSUNITS\n70\n4\n";
        var result = DxfImporter.Parse(Dxf(Line(0, 0, 100, 0), header));

        Assert.Equal(100, result.WidthMm, 6);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("units", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AMissingUnitDeclarationIsCalledOut()
    {
        // Silently guessing the scale of a cutting file is not acceptable.
        var result = DxfImporter.Parse(Dxf(Line(0, 0, 100, 0)));
        Assert.Contains(result.Warnings, w => w.Contains("units", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BlockReferencesAreExpandedAndPlaced()
    {
        var blocks = "0\nBLOCK\n2\nHOLE\n" + "0\nCIRCLE\n10\n0\n20\n0\n40\n5\n" + "0\nENDBLK\n";
        var entities = "0\nINSERT\n2\nHOLE\n10\n100\n20\n50\n" +
                       "0\nINSERT\n2\nHOLE\n10\n200\n20\n50\n";

        var result = DxfImporter.Parse(Dxf(entities, blocks: blocks));

        Assert.Equal(2, result.Paths.Count);
        Assert.Equal(100, result.Paths[0].Bounds.Center.X, 1);
        Assert.Equal(200, result.Paths[1].Bounds.Center.X, 1);
        Assert.Equal(10, result.Paths[0].Bounds.Width, 1);
    }

    [Fact]
    public void BlockReferencesHonourScaleAndRotation()
    {
        var blocks = "0\nBLOCK\n2\nBAR\n" + "0\nLINE\n10\n0\n20\n0\n11\n10\n21\n0\n" + "0\nENDBLK\n";
        var entities = "0\nINSERT\n2\nBAR\n10\n0\n20\n0\n41\n2\n42\n2\n50\n90\n";

        var path = Assert.Single(DxfImporter.Parse(Dxf(entities, blocks: blocks)).Paths);

        // Scaled to 20 mm and turned a quarter turn, so it now runs up the Y axis.
        Assert.Equal(0, path.Last.X, 3);
        Assert.Equal(20, path.Last.Y, 3);
    }

    [Fact]
    public void SplineFitPointsBecomeASmoothPath()
    {
        var entity = "0\nSPLINE\n70\n8\n" +
                     "11\n0\n21\n0\n11\n10\n21\n20\n11\n20\n21\n0\n11\n30\n21\n20\n";

        var spline = Assert.Single(DxfImporter.Parse(Dxf(entity)).Paths);

        Assert.True(spline.Count > 4, "the fit points should be smoothed into a curve");
        Assert.InRange(spline.Bounds.Width, 20, 31);
    }

    [Fact]
    public void UnsupportedEntitiesAreReportedRatherThanSilentlyDropped()
    {
        var result = DxfImporter.Parse(Dxf("0\nMTEXT\n10\n0\n20\n0\n1\nhello\n"));

        Assert.Empty(result.Paths);
        Assert.Contains(result.Warnings, w => w.Contains("MTEXT", StringComparison.Ordinal));
    }

    [Fact]
    public void ASingleStrayBlankLineDoesNotShiftEveryPair()
    {
        // A blank line desynchronises the code/value pairing, and every coordinate
        // after it would be read as a group code.
        var text = Dxf(Line(0, 0, 50, 0)).Replace("0\nLINE\n", "\n0\nLINE\n", StringComparison.Ordinal);
        var result = DxfImporter.Parse(text);

        Assert.Single(result.Paths);
        Assert.Equal(50, result.WidthMm, 6);
    }

    [Fact]
    public void AnEmptyDrawingSaysSoRatherThanThrowing()
    {
        var result = DxfImporter.Parse(Dxf(string.Empty));

        Assert.Empty(result.Paths);
        Assert.Contains(result.Warnings, w => w.Contains("No drawable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MixedDrawingsImportEveryEntity()
    {
        var entities = Line(0, 0, 100, 0) +
                       "0\nCIRCLE\n10\n50\n20\n50\n40\n20\n" +
                       "0\nARC\n10\n0\n20\n50\n40\n15\n50\n0\n51\n180\n" +
                       "0\nLWPOLYLINE\n90\n3\n70\n0\n10\n0\n20\n80\n10\n40\n20\n80\n10\n40\n20\n100\n";

        var result = DxfImporter.Parse(Dxf(entities));
        Assert.Equal(4, result.Paths.Count);
    }
}
