using Emberline.Core.Geometry;

namespace Emberline.Core.Documents;

public enum WorkpieceShape
{
    /// <summary>No workpiece defined — the whole bed is fair game.</summary>
    None,
    Rectangle,
    Circle,
}

/// <summary>
/// The material actually on the bed.
///
/// A bed is 400 mm square; the thing being engraved is a 100 mm tile somewhere in
/// the middle of it. Without saying so, the canvas shows a large empty rectangle
/// and nothing catches artwork that overhangs the coaster — the job runs, the
/// laser fires into the honeycomb, and the first sign of trouble is the smell.
///
/// Defining it means the boundary is drawn where the material is, artwork centres
/// on the workpiece rather than the bed, and validation can say when something
/// falls outside it.
/// </summary>
public sealed record Workpiece
{
    public WorkpieceShape Shape { get; init; } = WorkpieceShape.None;

    /// <summary>Full width in millimetres. For a circle this is the diameter.</summary>
    public double WidthMm { get; init; } = 100;

    public double HeightMm { get; init; } = 100;

    /// <summary>Rounded corners, for tiles and coasters that are not square-cornered.</summary>
    public double CornerRadiusMm { get; init; }

    /// <summary>Lower-left corner on the bed, in bed millimetres.</summary>
    public double XMm { get; init; }

    public double YMm { get; init; }

    /// <summary>What this blank is called, when it came from a preset.</summary>
    public string Name { get; init; } = "";

    public static readonly Workpiece None = new();

    public bool IsSet => Shape != WorkpieceShape.None && WidthMm > 0 && HeightMm > 0;

    public Rect2 Bounds => new(XMm, YMm, XMm + WidthMm, YMm + HeightMm);

    public string Summary => Shape switch
    {
        WorkpieceShape.Circle => $"{WidthMm:0.#} mm circle",
        WorkpieceShape.Rectangle when Math.Abs(WidthMm - HeightMm) < 0.05 => $"{WidthMm:0.#} mm square",
        WorkpieceShape.Rectangle => $"{WidthMm:0.#} × {HeightMm:0.#} mm",
        _ => "No workpiece",
    };

    /// <summary>Centre it on a bed of the given size.</summary>
    public Workpiece CentredOn(double bedWidthMm, double bedHeightMm) => this with
    {
        XMm = Math.Max(0, (bedWidthMm - WidthMm) / 2),
        YMm = Math.Max(0, (bedHeightMm - HeightMm) / 2),
    };

    /// <summary>The boundary, for drawing and for framing.</summary>
    public IReadOnlyList<Polyline> Outline(double toleranceMm = Curves.DefaultTolerance)
    {
        if (!IsSet) return [];

        if (Shape == WorkpieceShape.Circle)
        {
            // Ellipse takes radii and is centred on the origin; a workpiece is
            // described by its overall size and its lower-left corner.
            var circle = PathShape.Ellipse(WidthMm / 2, HeightMm / 2, toleranceMm);
            circle.Transform = Matrix2D.Translate(XMm + WidthMm / 2, YMm + HeightMm / 2);
            return circle.GetOutlines(toleranceMm);
        }

        var rect = PathShape.Rectangle(WidthMm, HeightMm, CornerRadiusMm);
        rect.Transform = Matrix2D.Translate(XMm, YMm);
        return rect.GetOutlines(toleranceMm);
    }

    /// <summary>
    /// How far a bounding box overhangs the workpiece, or zero when it fits.
    /// Measured against the rectangle even for a circle: a box that fits the
    /// bounding square of a disc can still overhang the disc itself, so this is
    /// deliberately the optimistic answer and the warning says so.
    /// </summary>
    public (double X, double Y) Overhang(Rect2 box)
    {
        if (!IsSet || box.IsEmpty) return (0, 0);

        var x = Math.Max(0, Math.Max(XMm - box.MinX, box.MaxX - (XMm + WidthMm)));
        var y = Math.Max(0, Math.Max(YMm - box.MinY, box.MaxY - (YMm + HeightMm)));
        return (x, y);
    }

    public bool Contains(Rect2 box)
    {
        var (x, y) = Overhang(box);
        return x <= 0.001 && y <= 0.001;
    }
}
