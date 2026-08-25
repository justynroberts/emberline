using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;
using OpenBurn.Core.Storage;
using OpenBurn.Core.Units;
using Xunit;

namespace OpenBurn.Core.Tests;

/// <summary>
/// Saving a design and getting the same one back.
///
/// Exported G-code is the output, with every decision already baked out of it. A
/// document has to carry the decisions: which layers, at what power, on what
/// material, with which tone curve on the photograph. If any of that is lost on
/// reopening then the file did not save the design, it saved a picture of it.
/// </summary>
public class DesignFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "openburn-designs", Guid.NewGuid().ToString("N"));

    public DesignFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Path_(string name) => System.IO.Path.Combine(_folder, name + DesignFile.Extension);

    private static Polyline Square(double size) => new(
    [
        new Vec2(0, 0), new Vec2(size, 0), new Vec2(size, size), new Vec2(0, size),
    ], closed: true);

    private static Design Sample()
    {
        var design = new Design { Name = "Coaster", DisplayUnit = LengthUnit.Millimetres };

        var engrave = Layer.CreateDefault(OperationKind.Fill, 0);
        engrave.Name = "Artwork";
        engrave.SpeedMmMin = 2400;
        engrave.PowerPercent = 32;
        engrave.LineIntervalMm = 0.08;
        engrave.AirAssist = true;

        var cut = Layer.CreateDefault(OperationKind.Cut, 1);
        cut.PowerPercent = 95;
        cut.Passes = 4;
        cut.MergeOverlaps = false;

        design.Layers.Add(engrave);
        design.Layers.Add(cut);

        var shape = new PathShape([Square(40)]) { Name = "Outline" };
        shape.Translate(new Vec2(30, 25));
        design.AddShape(shape, cut);

        design.Workpiece = new Workpiece
        {
            Shape = WorkpieceShape.Circle, WidthMm = 100, HeightMm = 100, XMm = 20, YMm = 30, Name = "Slate",
        };

        return design;
    }

    [Fact]
    public void ADesignSurvivesBeingSavedAndReopened()
    {
        var path = Path_("round-trip");
        var original = Sample();

        DesignFile.Save(original, path);
        var reopened = DesignFile.Load(path);

        Assert.Equal("Coaster", reopened.Name);
        Assert.Equal(2, reopened.Layers.Count);
        Assert.Single(reopened.Shapes);
    }

    [Fact]
    public void LayerSettingsComeBackExactly()
    {
        // These are the numbers somebody spent an afternoon finding.
        var path = Path_("layers");
        DesignFile.Save(Sample(), path);

        var layer = DesignFile.Load(path).Layers.First(l => l.Operation == OperationKind.Fill);

        Assert.Equal("Artwork", layer.Name);
        Assert.Equal(2400, layer.SpeedMmMin, 6);
        Assert.Equal(32, layer.PowerPercent, 6);
        Assert.Equal(0.08, layer.LineIntervalMm, 6);
        Assert.True(layer.AirAssist);

        var cut = DesignFile.Load(path).Layers.First(l => l.Operation == OperationKind.Cut);
        Assert.Equal(4, cut.Passes);
        Assert.False(cut.MergeOverlaps);
    }

    [Fact]
    public void ShapesComeBackWhereTheyWerePut()
    {
        var path = Path_("placement");
        var original = Sample();
        var before = original.Shapes[0].Bounds;

        DesignFile.Save(original, path);
        var after = DesignFile.Load(path).Shapes[0].Bounds;

        Assert.Equal(before.MinX, after.MinX, 6);
        Assert.Equal(before.MinY, after.MinY, 6);
        Assert.Equal(before.Width, after.Width, 6);
    }

    [Fact]
    public void ShapesStayOnTheirOwnLayer()
    {
        var path = Path_("layer-link");
        var original = Sample();
        var cutId = original.Layers.First(l => l.Operation == OperationKind.Cut).Id;

        DesignFile.Save(original, path);
        var reopened = DesignFile.Load(path);

        Assert.Equal(cutId, reopened.Shapes[0].LayerId);
        Assert.Equal(OperationKind.Cut, reopened.FindLayer(reopened.Shapes[0].LayerId)!.Operation);
    }

    [Fact]
    public void TheWorkpieceIsRemembered()
    {
        var path = Path_("workpiece");
        DesignFile.Save(Sample(), path);

        var workpiece = DesignFile.Load(path).Workpiece;

        Assert.True(workpiece.IsSet);
        Assert.Equal(WorkpieceShape.Circle, workpiece.Shape);
        Assert.Equal(100, workpiece.WidthMm, 6);
        Assert.Equal(20, workpiece.XMm, 6);
    }

    [Fact]
    public void APhotographAndItsToneCurveAreBothKept()
    {
        // The adjustments are the work. An image that reopens with default
        // brightness has lost everything that took time.
        var path = Path_("image");
        var design = new Design();
        design.Layers.Add(Layer.CreateDefault(OperationKind.Fill, 0));

        var pixels = new byte[16 * 12];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7 % 256);

        var image = new ImageShape(new RasterImage(16, 12, pixels), 80, 60) { Name = "Photo" };
        image.Adjustments = image.Adjustments with { Invert = true, Brightness = 20, Gamma = 1.8, WhiteClip = 240 };
        design.AddShape(image, design.Layers[0]);

        DesignFile.Save(design, path);
        var reopened = (ImageShape)DesignFile.Load(path).Shapes[0];

        Assert.Equal(16, reopened.Source.Width);
        Assert.Equal(pixels, reopened.Source.Pixels);
        Assert.True(reopened.Adjustments.Invert);
        Assert.Equal(20, reopened.Adjustments.Brightness, 6);
        Assert.Equal(1.8, reopened.Adjustments.Gamma, 6);
        Assert.Equal(240, reopened.Adjustments.WhiteClip);
        Assert.Equal(80, reopened.WidthMm, 6);
    }

    [Fact]
    public void TextKeepsBothItsWordingAndTheShapeItWasCutAs()
    {
        // A font missing on the next machine would otherwise silently change the
        // letterforms, and the file would no longer describe what was cut.
        var path = Path_("text");
        var design = new Design();
        design.Layers.Add(Layer.CreateDefault(OperationKind.Engrave, 0));

        var text = new TextShape { Text = "Hello", FontFamily = "Some Font", FontSizeMm = 14 };
        text.SetOutlines([Square(9)]);
        design.AddShape(text, design.Layers[0]);

        DesignFile.Save(design, path);
        var reopened = (TextShape)DesignFile.Load(path).Shapes[0];

        Assert.Equal("Hello", reopened.Text);
        Assert.Equal("Some Font", reopened.FontFamily);
        Assert.Equal(14, reopened.FontSizeMm, 6);
        Assert.Single(reopened.Outlines);
        Assert.Equal(9, reopened.Outlines[0].Bounds.Width, 6);
    }

    [Fact]
    public void GroupsKeepTheirChildren()
    {
        var path = Path_("group");
        var design = new Design();
        design.Layers.Add(Layer.CreateDefault(OperationKind.Cut, 0));

        var group = new GroupShape([new PathShape([Square(10)]), new PathShape([Square(20)])]) { Name = "Pair" };
        design.AddShape(group, design.Layers[0]);

        DesignFile.Save(design, path);
        var reopened = (GroupShape)DesignFile.Load(path).Shapes[0];

        Assert.Equal(2, reopened.Children.Count);
    }

    [Fact]
    public void TheFileIsSelfContained()
    {
        // It has to survive being emailed to somebody else, which means no paths
        // to anything on this machine.
        var path = Path_("portable");
        var design = new Design();
        design.Layers.Add(Layer.CreateDefault(OperationKind.Fill, 0));
        design.AddShape(new ImageShape(new RasterImage(4, 4, new byte[16]), 10, 10)
        {
            SourcePath = "/Users/someone/Pictures/holiday.png",
        }, design.Layers[0]);

        DesignFile.Save(design, path);

        Assert.DoesNotContain("/Users/someone", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void SavingRemembersWhereItWentSoTheNextSaveDoesNotAsk()
    {
        var path = Path_("filepath");
        var design = Sample();

        DesignFile.Save(design, path);

        Assert.Equal(path, design.FilePath);
        Assert.Equal(path, DesignFile.Load(path).FilePath);
    }

    [Fact]
    public void AFileFromANewerVersionIsRefusedRatherThanMisread()
    {
        var path = Path_("from-the-future");
        File.WriteAllText(path, """{"version": 9999, "name": "later"}""");

        var error = Assert.Throws<InvalidDataException>(() => DesignFile.Load(path));
        Assert.Contains("newer version", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SomethingThatIsNotADesignIsRefusedClearly()
    {
        var path = Path_("nonsense");
        File.WriteAllText(path, "this is not json at all");

        Assert.ThrowsAny<Exception>(() => DesignFile.Load(path));
    }

    [Fact]
    public void ADesignWithNoLayersStillOpensUsable()
    {
        // A file that produces a document you cannot draw on is worse than one
        // that repairs itself.
        var path = Path_("empty");
        File.WriteAllText(path, """{"version": 1, "name": "bare"}""");

        var design = DesignFile.Load(path);

        Assert.NotEmpty(design.Layers);
        Assert.Equal("bare", design.Name);
    }

    [Fact]
    public void AShapePointingAtAMissingLayerLandsSomewhereVisible()
    {
        var path = Path_("orphan");
        var design = Sample();
        design.Shapes[0].LayerId = "a-layer-that-is-not-in-this-file";

        DesignFile.Save(design, path);
        var reopened = DesignFile.Load(path);

        Assert.Contains(reopened.Layers, l => l.Id == reopened.Shapes[0].LayerId);
    }
}
