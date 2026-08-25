using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;
using OpenBurn.Core.Units;

namespace OpenBurn.Core.Storage;

/// <summary>
/// Saving and reopening a design.
///
/// Until this existed OpenBurn could export G-code and nothing else, which meant a
/// design — its layers, its workpiece, the tone curve painstakingly dialled in on a
/// photograph — survived exactly as long as the window stayed open. Exported G-code
/// is not a document: it is the output, with every decision already baked out of it
/// and no way back.
///
/// The file is JSON, one object, self-contained. Images are embedded rather than
/// referenced, because a design that breaks when somebody tidies their Downloads
/// folder is not saved in any useful sense. It costs size and buys a file that can
/// be emailed to somebody else and still open.
/// </summary>
public static class DesignFile
{
    public const string Extension = ".openburn";

    /// <summary>Bumped when the shape changes, so an old file can be migrated rather than refused.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },

        // Hand-edited files and anything written by another tool should still open;
        // insisting on exact casing turns a readable file into an unreadable one for
        // no benefit.
        PropertyNameCaseInsensitive = true,
    };

    public static void Save(Design design, string path)
    {
        var document = Capture(design);

        // Write beside the target and move into place, so a failure halfway
        // through leaves the previous file intact rather than a truncated one.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options));
        File.Move(temporary, path, overwrite: true);

        design.FilePath = path;
    }

    public static Design Load(string path)
    {
        var document = JsonSerializer.Deserialize<DesignDocument>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("That file is not an OpenBurn design.");

        if (document.Version > CurrentVersion)
        {
            throw new InvalidDataException(
                $"That design was saved by a newer version of OpenBurn (format {document.Version}, this build reads {CurrentVersion}).");
        }

        var design = Restore(document);
        design.FilePath = path;
        return design;
    }

    // ------------------------------------------------------------- capture

    private static DesignDocument Capture(Design design) => new()
    {
        Version = CurrentVersion,
        Name = design.Name,
        DisplayUnit = design.DisplayUnit,
        Workpiece = design.Workpiece.IsSet ? CaptureWorkpiece(design.Workpiece) : null,
        Layers = [.. design.Layers.Select(CaptureLayer)],
        Shapes = [.. design.Shapes.Select(CaptureShape)],
    };

    /// <summary>
    /// Only the stored fields. Serialising the domain record directly walks its
    /// computed properties — Bounds gives a Center, a Vec2 has a Normalized, and
    /// that is a Vec2 — so the serialiser recurses until it gives up.
    /// </summary>
    private static WorkpieceDocument CaptureWorkpiece(Workpiece w) => new()
    {
        Shape = w.Shape,
        WidthMm = w.WidthMm,
        HeightMm = w.HeightMm,
        CornerRadiusMm = w.CornerRadiusMm,
        XMm = w.XMm,
        YMm = w.YMm,
        Name = w.Name,
    };

    private static Workpiece RestoreWorkpiece(WorkpieceDocument? d) => d is null
        ? Workpiece.None
        : new Workpiece
        {
            Shape = d.Shape,
            WidthMm = d.WidthMm,
            HeightMm = d.HeightMm,
            CornerRadiusMm = d.CornerRadiusMm,
            XMm = d.XMm,
            YMm = d.YMm,
            Name = d.Name ?? "",
        };

    private static LayerDocument CaptureLayer(Layer layer) => new()
    {
        Id = layer.Id,
        Name = layer.Name,
        Operation = layer.Operation,
        SpeedMmMin = layer.SpeedMmMin,
        PowerPercent = layer.PowerPercent,
        MinPowerPercent = layer.MinPowerPercent,
        Passes = layer.Passes,
        LineIntervalMm = layer.LineIntervalMm,
        AirAssist = layer.AirAssist,
        MergeOverlaps = layer.MergeOverlaps,
        Enabled = layer.Enabled,
        Order = layer.Order,
        ColorHex = layer.ColorHex,
        FillStrategy = layer.FillStrategy,
        FillAngleDeg = layer.FillAngleDeg,
        Bidirectional = layer.Bidirectional,
        OverscanMm = layer.OverscanMm,
        ZOffsetMm = layer.ZOffsetMm,
    };

    private static ShapeDocument CaptureShape(Shape shape)
    {
        var document = new ShapeDocument
        {
            Id = shape.Id,
            Name = shape.Name,
            LayerId = shape.LayerId,
            Transform = [shape.Transform.A, shape.Transform.B, shape.Transform.C,
                         shape.Transform.D, shape.Transform.E, shape.Transform.F],
            Locked = shape.Locked,
            Visible = shape.Visible,
        };

        switch (shape)
        {
            case PathShape path:
                document.Kind = ShapeKind.Path;
                document.Paths = [.. path.Paths.Select(CapturePolyline)];
                break;

            case TextShape text:
                document.Kind = ShapeKind.Text;
                document.Text = text.Text;
                document.FontFamily = text.FontFamily;
                document.FontSizeMm = text.FontSizeMm;
                document.Bold = text.Bold;
                document.Italic = text.Italic;
                document.LetterSpacingMm = text.LetterSpacingMm;
                document.LineHeightFactor = text.LineHeightFactor;
                document.Alignment = text.Alignment;
                // The outlines are kept as well as the settings: a font that is not
                // installed on the next machine would otherwise silently change the
                // shape of the letters, and the file would no longer be what was cut.
                document.Paths = [.. text.Outlines.Select(CapturePolyline)];
                break;

            case ImageShape image:
                document.Kind = ShapeKind.Image;
                document.WidthMm = image.WidthMm;
                document.HeightMm = image.HeightMm;
                document.Adjustments = image.Adjustments;
                document.PixelWidth = image.Source.Width;
                document.PixelHeight = image.Source.Height;
                document.Pixels = Convert.ToBase64String(image.Source.Pixels);
                break;

            case GroupShape group:
                document.Kind = ShapeKind.Group;
                document.Children = [.. group.Children.Select(CaptureShape)];
                break;

            default:
                // An unknown shape is still worth keeping as the outline it draws:
                // better a design that reopens flattened than one that loses a piece.
                document.Kind = ShapeKind.Path;
                document.Paths = [.. shape.GetOutlines().Select(CapturePolyline)];
                break;
        }

        return document;
    }

    private static PolylineDocument CapturePolyline(Polyline path) => new()
    {
        Closed = path.IsClosed,
        Points = [.. path.Points.SelectMany(p => new[] { p.X, p.Y })],
    };

    // ------------------------------------------------------------- restore

    private static Design Restore(DesignDocument document)
    {
        var design = new Design
        {
            Name = document.Name ?? "Untitled",
            DisplayUnit = document.DisplayUnit,
            Workpiece = RestoreWorkpiece(document.Workpiece),
        };

        foreach (var layer in document.Layers ?? []) design.Layers.Add(RestoreLayer(layer));

        // A design with no layers cannot hold a shape, and a file that produces an
        // unusable document is worse than one that repairs itself.
        if (design.Layers.Count == 0) design.Layers.Add(Layer.CreateDefault(OperationKind.Engrave, 0));

        foreach (var shape in document.Shapes ?? [])
        {
            var restored = RestoreShape(shape);
            if (restored is null) continue;

            // Shapes pointing at a layer that is not in the file land on the first
            // one rather than becoming invisible.
            if (design.Layers.All(l => l.Id != restored.LayerId)) restored.LayerId = design.Layers[0].Id;
            design.Shapes.Add(restored);
        }

        return design;
    }

    private static Layer RestoreLayer(LayerDocument d)
    {
        var layer = new Layer { Id = d.Id ?? Guid.NewGuid().ToString("N") };
        layer.Name = d.Name ?? "Layer";
        layer.Operation = d.Operation;
        layer.SpeedMmMin = d.SpeedMmMin;
        layer.PowerPercent = d.PowerPercent;
        layer.MinPowerPercent = d.MinPowerPercent;
        layer.Passes = d.Passes;
        layer.LineIntervalMm = d.LineIntervalMm;
        layer.AirAssist = d.AirAssist;
        layer.MergeOverlaps = d.MergeOverlaps;
        layer.Enabled = d.Enabled;
        layer.Order = d.Order;
        layer.ColorHex = d.ColorHex ?? "#D9531E";
        layer.FillStrategy = d.FillStrategy;
        layer.FillAngleDeg = d.FillAngleDeg;
        layer.Bidirectional = d.Bidirectional;
        layer.OverscanMm = d.OverscanMm;
        layer.ZOffsetMm = d.ZOffsetMm;
        return layer;
    }

    private static Shape? RestoreShape(ShapeDocument d)
    {
        Shape shape;

        switch (d.Kind)
        {
            case ShapeKind.Text:
            {
                var text = new TextShape
                {
                    Text = d.Text ?? "",
                    FontFamily = d.FontFamily ?? "",
                    FontSizeMm = d.FontSizeMm ?? 10,
                    Bold = d.Bold,
                    Italic = d.Italic,
                    LetterSpacingMm = d.LetterSpacingMm ?? 0,
                    LineHeightFactor = d.LineHeightFactor ?? 1.2,
                    Alignment = d.Alignment,
                };
                if (d.Paths is { Count: > 0 }) text.SetOutlines(d.Paths.Select(RestorePolyline));
                shape = text;
                break;
            }

            case ShapeKind.Image:
            {
                if (d.Pixels is null || d.PixelWidth is not { } w || d.PixelHeight is not { } h) return null;

                var pixels = Convert.FromBase64String(d.Pixels);
                if (pixels.Length != w * h) return null;

                shape = new ImageShape(new RasterImage(w, h, pixels), d.WidthMm ?? w, d.HeightMm ?? h)
                {
                    Adjustments = d.Adjustments ?? ImageAdjustments.Default,
                };
                break;
            }

            case ShapeKind.Group:
            {
                var group = new GroupShape();
                foreach (var child in d.Children ?? [])
                {
                    if (RestoreShape(child) is { } restored) group.Add(restored);
                }
                shape = group;
                break;
            }

            default:
                shape = new PathShape((d.Paths ?? []).Select(RestorePolyline));
                break;
        }

        shape.Name = d.Name ?? "Shape";
        shape.LayerId = d.LayerId ?? "";
        shape.Locked = d.Locked;
        shape.Visible = d.Visible;

        if (d.Transform is { Length: 6 } t) shape.Transform = new Matrix2D(t[0], t[1], t[2], t[3], t[4], t[5]);

        return shape;
    }

    private static Polyline RestorePolyline(PolylineDocument d)
    {
        var path = new Polyline { IsClosed = d.Closed };
        var points = d.Points ?? [];
        for (var i = 0; i + 1 < points.Length; i += 2) path.Add(points[i], points[i + 1]);
        return path;
    }

    // ---------------------------------------------------------- the shapes on disk

    private enum ShapeKind { Path, Text, Image, Group }

    private sealed class DesignDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public string? Name { get; set; }
        public LengthUnit DisplayUnit { get; set; } = LengthUnit.Millimetres;
        public WorkpieceDocument? Workpiece { get; set; }
        public List<LayerDocument>? Layers { get; set; }
        public List<ShapeDocument>? Shapes { get; set; }
    }

    private sealed class WorkpieceDocument
    {
        public WorkpieceShape Shape { get; set; }
        public double WidthMm { get; set; } = 100;
        public double HeightMm { get; set; } = 100;
        public double CornerRadiusMm { get; set; }
        public double XMm { get; set; }
        public double YMm { get; set; }
        public string? Name { get; set; }
    }

    private sealed class LayerDocument
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public OperationKind Operation { get; set; }
        public double SpeedMmMin { get; set; } = 1000;
        public double PowerPercent { get; set; } = 50;
        public double MinPowerPercent { get; set; }
        public int Passes { get; set; } = 1;
        public double LineIntervalMm { get; set; } = 0.1;
        public bool AirAssist { get; set; }
        public bool MergeOverlaps { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public int Order { get; set; }
        public string? ColorHex { get; set; }
        public FillStrategy FillStrategy { get; set; }
        public double FillAngleDeg { get; set; }
        public bool Bidirectional { get; set; } = true;
        public double OverscanMm { get; set; }
        public double ZOffsetMm { get; set; }
    }

    private sealed class ShapeDocument
    {
        public ShapeKind Kind { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? LayerId { get; set; }
        public double[]? Transform { get; set; }
        public bool Locked { get; set; }
        public bool Visible { get; set; } = true;

        public List<PolylineDocument>? Paths { get; set; }

        public string? Text { get; set; }
        public string? FontFamily { get; set; }
        public double? FontSizeMm { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public double? LetterSpacingMm { get; set; }
        public double? LineHeightFactor { get; set; }
        public TextAlignment Alignment { get; set; }

        public double? WidthMm { get; set; }
        public double? HeightMm { get; set; }
        public ImageAdjustments? Adjustments { get; set; }
        public int? PixelWidth { get; set; }
        public int? PixelHeight { get; set; }
        public string? Pixels { get; set; }

        public List<ShapeDocument>? Children { get; set; }
    }

    private sealed class PolylineDocument
    {
        public bool Closed { get; set; }
        public double[]? Points { get; set; }
    }
}
