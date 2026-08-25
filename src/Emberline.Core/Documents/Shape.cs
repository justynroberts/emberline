using System.ComponentModel;
using System.Runtime.CompilerServices;
using Emberline.Core.Geometry;

namespace Emberline.Core.Documents;

/// <summary>
/// Everything on the canvas. A shape owns its geometry in local coordinates and a
/// transform that places it on the bed, which is what makes non-destructive
/// move/scale/rotate possible — the source geometry is never rewritten.
/// </summary>
public abstract class Shape : INotifyPropertyChanged
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    private string _name = "Shape";
    private string _layerId = string.Empty;
    private Matrix2D _transform = Matrix2D.Identity;
    private bool _locked;
    private bool _visible = true;

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>Which operation layer this shape belongs to.</summary>
    public string LayerId { get => _layerId; set => Set(ref _layerId, value); }

    /// <summary>Local space → bed space, in millimetres.</summary>
    public Matrix2D Transform { get => _transform; set => Set(ref _transform, value); }

    public bool Locked { get => _locked; set => Set(ref _locked, value); }
    public bool Visible { get => _visible; set => Set(ref _visible, value); }

    /// <summary>Bounds in local space, before <see cref="Transform"/>.</summary>
    public abstract Rect2 LocalBounds { get; }

    /// <summary>
    /// Bounds on the bed. Computed from the four transformed corners, which is
    /// correct for rotation as well as scale.
    /// </summary>
    public Rect2 Bounds
    {
        get
        {
            var b = LocalBounds;
            if (b.IsEmpty) return b;
            return Rect2.FromPoints(
            [
                Transform.Apply(b.MinX, b.MinY),
                Transform.Apply(b.MaxX, b.MinY),
                Transform.Apply(b.MaxX, b.MaxY),
                Transform.Apply(b.MinX, b.MaxY),
            ]);
        }
    }

    /// <summary>
    /// Outline geometry in bed coordinates. For a path this is the path; for an
    /// image it is the frame; for text it is the glyph outlines.
    /// </summary>
    public abstract IReadOnlyList<Polyline> GetOutlines(double tolerance = Curves.DefaultTolerance);

    public abstract Shape Clone();

    public void Translate(Vec2 delta) => Transform = Matrix2D.Translate(delta) * Transform;

    public void RotateAbout(double degrees, Vec2 pivot) => Transform = Matrix2D.RotateAbout(degrees, pivot) * Transform;

    public void ScaleAbout(double sx, double sy, Vec2 pivot) =>
        Transform = Matrix2D.Translate(pivot) * Matrix2D.Scale(sx, sy) * Matrix2D.Translate(-pivot) * Transform;

    /// <summary>Mirror across a vertical (or horizontal) line through <paramref name="pivot"/>.</summary>
    public void Mirror(bool horizontal, Vec2 pivot) =>
        ScaleAbout(horizontal ? -1 : 1, horizontal ? 1 : -1, pivot);

    /// <summary>Move so the shape's bounds land at the given bottom-left corner.</summary>
    public void MoveTo(Vec2 bottomLeft)
    {
        var b = Bounds;
        if (b.IsEmpty) return;
        Translate(bottomLeft - b.Min);
    }

    /// <summary>Resize so the bounds match the requested millimetre size.</summary>
    public void ResizeTo(double widthMm, double heightMm, bool keepAspect)
    {
        var b = Bounds;
        if (b.IsEmpty || b.Width < 1e-9 || b.Height < 1e-9) return;

        var sx = widthMm / b.Width;
        var sy = heightMm / b.Height;
        if (keepAspect)
        {
            var s = Math.Min(sx, sy);
            sx = s;
            sy = s;
        }
        ScaleAbout(sx, sy, b.Min);
    }

    protected void CopyBaseTo(Shape target)
    {
        target.Name = Name;
        target.LayerId = LayerId;
        target.Transform = Transform;
        target.Locked = Locked;
        target.Visible = Visible;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }

    protected void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
