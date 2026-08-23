using System.Collections.ObjectModel;
using OpenBurn.Core.Geometry;
using OpenBurn.Core.Units;

namespace OpenBurn.Core.Documents;

/// <summary>
/// The document. Shapes live in bed coordinates in millimetres with Y pointing up,
/// matching the machine, so what the canvas shows and what the laser does are the
/// same numbers — no hidden flip anywhere in the pipeline.
/// </summary>
public sealed class Design
{
    public ObservableCollection<Layer> Layers { get; } = [];
    public ObservableCollection<Shape> Shapes { get; } = [];

    public string Name { get; set; } = "Untitled";
    public LengthUnit DisplayUnit { get; set; } = LengthUnit.Millimetres;

    /// <summary>Path this design was loaded from or last saved to.</summary>
    public string? FilePath { get; set; }

    /// <summary>The material on the bed, when it has been described. See <see cref="Workpiece"/>.</summary>
    public Workpiece Workpiece { get; set; } = Workpiece.None;

    public static Design CreateDefault()
    {
        var d = new Design();
        d.Layers.Add(Layer.CreateDefault(OperationKind.Engrave, 0));
        d.Layers.Add(Layer.CreateDefault(OperationKind.Cut, 1));
        return d;
    }

    public Layer? FindLayer(string id) => Layers.FirstOrDefault(l => l.Id == id);

    /// <summary>The layer new shapes land on. Never returns null once a design exists.</summary>
    public Layer DefaultLayer
    {
        get
        {
            if (Layers.Count == 0) Layers.Add(Layer.CreateDefault(OperationKind.Engrave, 0));
            return Layers[0];
        }
    }

    public void AddShape(Shape shape, Layer? layer = null)
    {
        shape.LayerId = (layer ?? DefaultLayer).Id;
        Shapes.Add(shape);
    }

    public IEnumerable<Shape> ShapesOnLayer(Layer layer) =>
        Shapes.Where(s => s.LayerId == layer.Id && s.Visible);

    /// <summary>Layers in the order they will be sent to the machine.</summary>
    public IEnumerable<Layer> OrderedLayers => Layers.Where(l => l.Enabled).OrderBy(l => l.Order);

    public Rect2 Bounds
    {
        get
        {
            var r = Rect2.Empty;
            foreach (var s in Shapes)
            {
                if (s.Visible) r = r.Union(s.Bounds);
            }
            return r;
        }
    }

    public Rect2 SelectionBounds(IEnumerable<Shape> selection)
    {
        var r = Rect2.Empty;
        foreach (var s in selection) r = r.Union(s.Bounds);
        return r;
    }

    /// <summary>Delete a layer, moving anything on it to the fallback layer rather than losing it.</summary>
    public void RemoveLayer(Layer layer)
    {
        if (Layers.Count <= 1) return;
        Layers.Remove(layer);
        var fallback = DefaultLayer;
        foreach (var s in Shapes.Where(s => s.LayerId == layer.Id)) s.LayerId = fallback.Id;
    }

    public void RemoveShape(Shape shape) => Shapes.Remove(shape);

    /// <summary>Everything that would be burned, flattened to bed-space polylines. Used by framing and bounds checks.</summary>
    public IReadOnlyList<Polyline> AllOutlines(double tolerance = Curves.DefaultTolerance)
    {
        var result = new List<Polyline>();
        foreach (var layer in OrderedLayers)
        {
            foreach (var shape in ShapesOnLayer(layer)) result.AddRange(shape.GetOutlines(tolerance));
        }
        return result;
    }

    public Design Clone()
    {
        var copy = new Design { Name = Name, DisplayUnit = DisplayUnit, FilePath = FilePath };
        foreach (var l in Layers) copy.Layers.Add(l.Clone());
        // Layer ids change on clone, so remap shapes onto the corresponding new layer.
        var map = Layers.Select((l, i) => (Old: l.Id, New: copy.Layers[i].Id)).ToDictionary(t => t.Old, t => t.New);
        foreach (var s in Shapes)
        {
            var c = s.Clone();
            if (map.TryGetValue(s.LayerId, out var newId)) c.LayerId = newId;
            copy.Shapes.Add(c);
        }
        return copy;
    }
}
