using Emberline.Cam.Import;
using Emberline.Catalog;
using Emberline.Core.Documents;
using Emberline.Core.Geometry;

namespace Emberline.App.ViewModels;

/// <summary>How a piece of catalogue artwork should be burned.</summary>
public enum CatalogImportMode
{
    /// <summary>Darken the whole shape — the icon as it looks on screen.</summary>
    Etch,

    /// <summary>Follow the boundary only, to cut the shape out.</summary>
    Cut,
}

/// <summary>Importing artwork from the icon catalogue.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Size the catalogue window last imported at, so it is remembered within a session.</summary>
    public double CatalogSizeMm { get; set; } = 50;

    /// <summary>
    /// Add catalogue artwork to the design.
    ///
    /// Icons are drawn as filled regions, which is the opposite of how a laser
    /// thinks: it follows lines. Both readings are useful, so the caller says
    /// which. Etch puts the shape on a Fill layer, so the interior is darkened and
    /// the picture appears. Cut puts it on a Cut layer, so only the boundary is
    /// followed and the shape drops out.
    ///
    /// Either way the geometry is identical — what changes is the operation, which
    /// means the choice can be reversed afterwards by moving the shape to another
    /// layer rather than importing it again.
    /// </summary>
    public string AddCatalogArtwork(string svg, string name, CatalogImportMode mode, double sizeMm = 50)
    {
        if (string.IsNullOrWhiteSpace(svg)) return "Nothing to import.";

        SvgImportResult result;
        try
        {
            result = SvgImporter.Import(svg);
        }
        catch (Exception ex)
        {
            return $"That artwork could not be read: {ex.Message}";
        }

        if (result.Paths.Count == 0) return "That artwork contained no outlines.";

        var shape = new PathShape(result.Paths) { Name = name };
        var bounds = shape.LocalBounds;
        if (bounds.IsEmpty || bounds.Width <= 0.01) return "That artwork has no size.";

        // Scale to the size asked for, keeping the aspect ratio.
        var scale = sizeMm / Math.Max(bounds.Width, bounds.Height);
        if (scale > 0 && Math.Abs(scale - 1) > 0.001) shape.ScaleAbout(scale, scale, bounds.Center);

        var target = Design.Workpiece.IsSet
            ? Design.Workpiece.Bounds.Center
            : new Vec2(SelectedMachine.BedWidthMm / 2, SelectedMachine.BedHeightMm / 2);

        var placed = shape.Bounds;
        shape.Translate(new Vec2(target.X - placed.Center.X, target.Y - placed.Center.Y));

        var layer = LayerFor(mode);

        EditDocument($"Import {name}", () =>
        {
            Design.AddShape(shape, layer);
            Selection.Clear();
            Selection.Add(shape);
        });

        var final = shape.Bounds;
        var what = mode == CatalogImportMode.Etch ? "etch" : "cut";
        Console.AppendInfo($"Imported “{name}” to {what} — {final.Width:0.#} × {final.Height:0.#} mm on the {layer.Name} layer.");

        return $"Added “{name}” at {final.Width:0.#} × {final.Height:0.#} mm, set to {what}.";
    }

    /// <summary>
    /// Find a layer that does what was asked, or make one.
    ///
    /// Reusing an existing layer matters: importing six icons to etch should
    /// produce one layer with six shapes on it, not six layers that each have to
    /// be given the same speed and power.
    /// </summary>
    private Layer LayerFor(CatalogImportMode mode) =>
        LayerForOperation(mode == CatalogImportMode.Etch ? OperationKind.Fill : OperationKind.Cut);

    /// <summary>
    /// Find a layer that does what was asked, or make one. Shared by the artwork
    /// import and the text tool, so both land on the same layer rather than
    /// creating one each.
    /// </summary>
    public Layer LayerForOperation(OperationKind wanted)
    {
        var existing = Design.Layers.FirstOrDefault(l => l.Operation == wanted);
        if (existing is not null) return existing;

        var layer = Layer.CreateDefault(wanted, Design.Layers.Count);
        Design.Layers.Add(layer);
        RebuildLayers();
        return layer;
    }
}
