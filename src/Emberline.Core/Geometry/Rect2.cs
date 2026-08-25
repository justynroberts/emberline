namespace Emberline.Core.Geometry;

/// <summary>Axis-aligned bounding box in millimetres, Y-up (machine convention).</summary>
public readonly record struct Rect2(double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>An inverted box that swallows the first point added to it.</summary>
    public static readonly Rect2 Empty = new(double.PositiveInfinity, double.PositiveInfinity,
                                             double.NegativeInfinity, double.NegativeInfinity);

    public bool IsEmpty => !(MaxX >= MinX && MaxY >= MinY);
    public double Width => IsEmpty ? 0 : MaxX - MinX;
    public double Height => IsEmpty ? 0 : MaxY - MinY;
    public Vec2 Center => new((MinX + MaxX) / 2, (MinY + MaxY) / 2);
    public Vec2 Min => new(MinX, MinY);
    public Vec2 Max => new(MaxX, MaxY);
    public double Area => Width * Height;

    public static Rect2 FromSize(double x, double y, double w, double h) => new(x, y, x + w, y + h);

    public static Rect2 FromPoints(IEnumerable<Vec2> points)
    {
        var r = Empty;
        foreach (var p in points) r = r.Add(p);
        return r;
    }

    public Rect2 Add(Vec2 p) => Add(p.X, p.Y);

    public Rect2 Add(double x, double y) => new(
        Math.Min(MinX, x), Math.Min(MinY, y),
        Math.Max(MaxX, x), Math.Max(MaxY, y));

    public Rect2 Union(Rect2 other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return new Rect2(Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY),
                         Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));
    }

    public Rect2 Inflate(double amount) => IsEmpty
        ? this
        : new Rect2(MinX - amount, MinY - amount, MaxX + amount, MaxY + amount);

    public bool Contains(Vec2 p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

    public bool Contains(Rect2 other) =>
        !other.IsEmpty && other.MinX >= MinX && other.MaxX <= MaxX && other.MinY >= MinY && other.MaxY <= MaxY;

    public bool Intersects(Rect2 other) =>
        !IsEmpty && !other.IsEmpty && other.MinX <= MaxX && other.MaxX >= MinX && other.MinY <= MaxY && other.MaxY >= MinY;

    public override string ToString() => IsEmpty ? "empty" : $"[{MinX:0.##},{MinY:0.##} .. {MaxX:0.##},{MaxY:0.##}]";
}
