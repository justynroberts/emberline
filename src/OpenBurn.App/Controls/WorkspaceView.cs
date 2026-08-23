using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;
using OpenBurn.Core.Machines;
using OpenBurn.GCode;

// Avalonia has its own Design and Shape types; alias ours so the file reads
// naturally without fully-qualifying every use.
using Design = OpenBurn.Core.Documents.Design;
using Shape = OpenBurn.Core.Documents.Shape;

namespace OpenBurn.App.Controls;

/// <summary>
/// The bed, the artwork and the toolpath, on one pan-and-zoom canvas.
///
/// Two rendering decisions carry the whole thing:
///
///  * **Batched geometry.** A raster preview is hundreds of thousands of segments.
///    Drawing each one individually drops the canvas to single-figure frame rates,
///    so segments are bucketed into eight power bands and each band becomes one
///    <see cref="StreamGeometry"/>. Rebuilt only when the toolpath changes, never
///    on pan or zoom.
///  * **Millimetres throughout.** The view transform converts to pixels at the last
///    possible moment. Everything above this control thinks in millimetres, so
///    what the canvas shows and what the machine does are the same numbers.
/// </summary>
public sealed class WorkspaceView : Control
{
    public static readonly StyledProperty<MachineProfile?> MachineProperty =
        AvaloniaProperty.Register<WorkspaceView, MachineProfile?>(nameof(Machine));

    public static readonly StyledProperty<Design?> DesignProperty =
        AvaloniaProperty.Register<WorkspaceView, Design?>(nameof(Design));

    public static readonly StyledProperty<Toolpath?> ToolpathProperty =
        AvaloniaProperty.Register<WorkspaceView, Toolpath?>(nameof(Toolpath));

    public static readonly StyledProperty<Point?> HeadPositionProperty =
        AvaloniaProperty.Register<WorkspaceView, Point?>(nameof(HeadPosition));

    public static readonly StyledProperty<double> JobFractionProperty =
        AvaloniaProperty.Register<WorkspaceView, double>(nameof(JobFraction));

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<WorkspaceView, bool>(nameof(ShowGrid), true);

    public static readonly StyledProperty<bool> ShowTravelProperty =
        AvaloniaProperty.Register<WorkspaceView, bool>(nameof(ShowTravel));

    public static readonly StyledProperty<Shape?> SelectedShapeProperty =
        AvaloniaProperty.Register<WorkspaceView, Shape?>(nameof(SelectedShape));

    /// <summary>
    /// A rectified top-down photograph of the bed, drawn behind everything else.
    ///
    /// This is what turns positioning from a numeric exercise into a visual one:
    /// the operator sees their actual workpiece under their artwork and drags one
    /// onto the other.
    /// </summary>
    public static readonly StyledProperty<Bitmap?> BedImageProperty =
        AvaloniaProperty.Register<WorkspaceView, Bitmap?>(nameof(BedImage));

    public static readonly StyledProperty<double> BedImageOpacityProperty =
        AvaloniaProperty.Register<WorkspaceView, double>(nameof(BedImageOpacity), 0.85);

    public MachineProfile? Machine { get => GetValue(MachineProperty); set => SetValue(MachineProperty, value); }
    public Design? Design { get => GetValue(DesignProperty); set => SetValue(DesignProperty, value); }
    public Toolpath? Toolpath { get => GetValue(ToolpathProperty); set => SetValue(ToolpathProperty, value); }
    public Point? HeadPosition { get => GetValue(HeadPositionProperty); set => SetValue(HeadPositionProperty, value); }
    public double JobFraction { get => GetValue(JobFractionProperty); set => SetValue(JobFractionProperty, value); }
    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public bool ShowTravel { get => GetValue(ShowTravelProperty); set => SetValue(ShowTravelProperty, value); }
    public Shape? SelectedShape { get => GetValue(SelectedShapeProperty); set => SetValue(SelectedShapeProperty, value); }
    public Bitmap? BedImage { get => GetValue(BedImageProperty); set => SetValue(BedImageProperty, value); }
    public double BedImageOpacity { get => GetValue(BedImageOpacityProperty); set => SetValue(BedImageOpacityProperty, value); }

    /// <summary>Pixels per millimetre.</summary>
    public double Zoom { get; private set; } = 2.0;

    /// <summary>Pan offset in pixels.</summary>
    public Vector Pan { get; private set; }

    /// <summary>Cursor position in bed millimetres, for the coordinate readout.</summary>
    public event Action<Vec2>? CursorMoved;

    /// <summary>A shape was clicked, or the background was (null).</summary>
    public event Action<Shape?>? ShapePicked;

    /// <summary>Double-click on the bed, in millimetres — used for "move head here".</summary>
    public event Action<Vec2>? BedDoubleClicked;

    private const int PowerBuckets = 8;
    private readonly StreamGeometry?[] _burnGeometry = new StreamGeometry?[PowerBuckets];
    private StreamGeometry? _travelGeometry;
    private Toolpath? _cachedToolpath;

    private bool _panning;
    private Point _panStart;
    private Vector _panOrigin;

    static WorkspaceView()
    {
        AffectsRender<WorkspaceView>(MachineProperty, DesignProperty, ToolpathProperty, HeadPositionProperty,
                                     JobFractionProperty, ShowGridProperty, ShowTravelProperty, SelectedShapeProperty,
                                     BedImageProperty, BedImageOpacityProperty);
    }

    private bool _hasFitted;

    public WorkspaceView()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>
    /// Fit the bed the first time the control is given a real size. Doing it in the
    /// constructor or on Opened is too early — Bounds is still zero, so the fit
    /// silently computes nothing and the bed lands in the corner.
    /// </summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_hasFitted || Bounds.Width < 50 || Bounds.Height < 50) return;
        _hasFitted = true;
        ZoomToFitBed();
    }

    // ------------------------------------------------------------- transform

    /// <summary>Bed millimetres to control pixels. Y is flipped: the bed's Y grows up, the screen's grows down.</summary>
    public Point ToPixels(double xMm, double yMm) =>
        new(xMm * Zoom + Pan.X, Bounds.Height - (yMm * Zoom + Pan.Y));

    public Vec2 ToMillimetres(Point pixel) =>
        new((pixel.X - Pan.X) / Zoom, (Bounds.Height - pixel.Y - Pan.Y) / Zoom);

    /// <summary>
    /// Inset of the floating chrome — tool rail, inspector, status bar — so a fit
    /// centres the bed in the part of the canvas the operator can actually see
    /// rather than in the part hidden behind a panel.
    /// </summary>
    public Thickness ChromeInset { get; set; } = new(90, 100, 356, 56);

    /// <summary>Fit the bed in the visible area with a comfortable margin.</summary>
    public void ZoomToFitBed()
    {
        var machine = Machine;
        if (machine is null) return;
        FitTo(0, 0, machine.BedWidthMm, machine.BedHeightMm);
    }

    private void FitTo(double minX, double minY, double width, double height)
    {
        if (Bounds.Width < 10 || Bounds.Height < 10 || width <= 0 || height <= 0) return;

        const double margin = 28;
        var usableWidth = Bounds.Width - ChromeInset.Left - ChromeInset.Right - margin * 2;
        var usableHeight = Bounds.Height - ChromeInset.Top - ChromeInset.Bottom - margin * 2;
        if (usableWidth < 40 || usableHeight < 40) return;

        Zoom = Math.Clamp(Math.Min(usableWidth / width, usableHeight / height), 0.05, 80);

        // Centre of the visible region, in control pixels.
        var centreX = ChromeInset.Left + margin + usableWidth / 2;
        var centreY = ChromeInset.Top + margin + usableHeight / 2;

        Pan = new Vector(
            centreX - (minX + width / 2) * Zoom,
            Bounds.Height - centreY - (minY + height / 2) * Zoom);

        InvalidateVisual();
    }

    /// <summary>Fit the artwork rather than the whole bed.</summary>
    public void ZoomToFitContent()
    {
        var bounds = Design?.Bounds ?? Rect2.Empty;
        if (bounds.IsEmpty || bounds.Width < 0.01 || bounds.Height < 0.01)
        {
            ZoomToFitBed();
            return;
        }
        FitTo(bounds.MinX, bounds.MinY, bounds.Width, bounds.Height);
    }

    /// <summary>Zoom about a pivot, keeping whatever is under it stationary.</summary>
    public void ZoomBy(double factor, Point? centre = null)
    {
        var pivot = centre ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        var before = ToMillimetres(pivot);

        Zoom = Math.Clamp(Zoom * factor, 0.05, 80);

        // Solve the pan that puts `before` back under the pivot at the new zoom.
        Pan = new Vector(pivot.X - before.X * Zoom, Bounds.Height - pivot.Y - before.Y * Zoom);
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- input

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ZoomBy(e.Delta.Y > 0 ? 1.12 : 1 / 1.12, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var point = e.GetCurrentPoint(this);
        var position = point.Position;

        // Middle button, or space held, pans. Left picks.
        var wantsPan = point.Properties.IsMiddleButtonPressed ||
                       e.KeyModifiers.HasFlag(KeyModifiers.Alt) ||
                       point.Properties.IsRightButtonPressed;

        if (wantsPan)
        {
            _panning = true;
            _panStart = position;
            _panOrigin = Pan;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2)
            {
                BedDoubleClicked?.Invoke(ToMillimetres(position));
                e.Handled = true;
                return;
            }

            ShapePicked?.Invoke(HitTest(ToMillimetres(position)));
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var position = e.GetPosition(this);
        CursorMoved?.Invoke(ToMillimetres(position));

        if (!_panning) return;
        var delta = position - _panStart;
        Pan = new Vector(_panOrigin.X + delta.X, _panOrigin.Y - delta.Y);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        e.Pointer.Capture(null);
    }

    private Shape? HitTest(Vec2 mm)
    {
        var design = Design;
        if (design is null) return null;

        // Topmost first, with a tolerance that scales with zoom so small shapes stay
        // clickable when zoomed out.
        var tolerance = 4 / Math.Max(Zoom, 0.01);
        for (var i = design.Shapes.Count - 1; i >= 0; i--)
        {
            var shape = design.Shapes[i];
            if (!shape.Visible) continue;
            if (shape.Bounds.Inflate(tolerance).Contains(new Vec2(mm.X, mm.Y))) return shape;
        }
        return null;
    }

    // --------------------------------------------------------------- render

    public override void Render(DrawingContext context)
    {
        var machine = Machine;
        var bg = Brush(Application.Current, "BgSunken", Colors.Black);
        context.FillRectangle(bg, new Rect(Bounds.Size));

        if (machine is null) return;

        DrawBed(context, machine);
        DrawBedImage(context, machine);
        if (ShowGrid) DrawGrid(context, machine);
        DrawShapes(context);
        DrawToolpath(context);
        DrawSelection(context);
        DrawOrigin(context);
        DrawHead(context);
    }

    private void DrawBed(DrawingContext context, MachineProfile machine)
    {
        var topLeft = ToPixels(0, machine.BedHeightMm);
        var bottomRight = ToPixels(machine.BedWidthMm, 0);
        var rect = new Rect(topLeft, bottomRight);

        context.DrawRectangle(
            Brush(Application.Current, "BedFill", Colors.White),
            new Pen(Brush(Application.Current, "BedEdge", Colors.Gray), 1.25),
            rect,
            8, 8);
    }

    /// <summary>
    /// Draw the camera view stretched across the bed. The image is already
    /// rectified and scaled to the bed, so this is a straight blit into the bed
    /// rectangle — no transform guesswork at draw time.
    /// </summary>
    private void DrawBedImage(DrawingContext context, MachineProfile machine)
    {
        if (BedImage is not { } bitmap) return;

        var topLeft = ToPixels(0, machine.BedHeightMm);
        var bottomRight = ToPixels(machine.BedWidthMm, 0);
        var target = new Rect(topLeft, bottomRight);

        using (context.PushOpacity(Math.Clamp(BedImageOpacity, 0, 1)))
        {
            context.DrawImage(bitmap, new Rect(bitmap.Size), target);
        }
    }

    private void DrawGrid(DrawingContext context, MachineProfile machine)
    {
        // Two densities: 10 mm minor, 50 mm major. The minor grid disappears when
        // zoomed out far enough that it would just be noise.
        var minorPen = new Pen(Brush(Application.Current, "GridMinor", Colors.Gray), 1);
        var majorPen = new Pen(Brush(Application.Current, "GridMajor", Colors.Gray), 1);

        var showMinor = Zoom > 1.4;

        for (double x = 0; x <= machine.BedWidthMm + 0.01; x += 10)
        {
            var major = Math.Abs(x % 50) < 0.01;
            if (!major && !showMinor) continue;
            var p0 = ToPixels(x, 0);
            var p1 = ToPixels(x, machine.BedHeightMm);
            context.DrawLine(major ? majorPen : minorPen, p0, p1);
        }

        for (double y = 0; y <= machine.BedHeightMm + 0.01; y += 10)
        {
            var major = Math.Abs(y % 50) < 0.01;
            if (!major && !showMinor) continue;
            var p0 = ToPixels(0, y);
            var p1 = ToPixels(machine.BedWidthMm, y);
            context.DrawLine(major ? majorPen : minorPen, p0, p1);
        }
    }

    private void DrawOrigin(DrawingContext context)
    {
        var origin = ToPixels(0, 0);
        var ember = Brush(Application.Current, "Ember", Colors.OrangeRed);
        var cyan = Brush(Application.Current, "Cyan", Colors.Cyan);

        context.DrawLine(new Pen(ember, 2), origin, origin + new Vector(18, 0));
        context.DrawLine(new Pen(cyan, 2), origin, origin - new Vector(0, 18));
        context.DrawEllipse(ember, null, origin, 3, 3);
    }

    private void DrawToolpath(DrawingContext context)
    {
        var toolpath = Toolpath;
        if (toolpath is null || toolpath.Count == 0) return;

        if (!ReferenceEquals(_cachedToolpath, toolpath)) RebuildToolpathGeometry(toolpath);

        if (ShowTravel && _travelGeometry is not null)
        {
            var pen = new Pen(Brush(Application.Current, "Travel", Colors.Teal), 0.75)
            {
                DashStyle = new DashStyle([3, 3], 0),
            };
            using (context.PushTransform(BuildRenderTransform()))
            {
                context.DrawGeometry(null, pen, _travelGeometry);
            }
        }

        using (context.PushTransform(BuildRenderTransform()))
        {
            for (var bucket = 0; bucket < PowerBuckets; bucket++)
            {
                var geometry = _burnGeometry[bucket];
                if (geometry is null) continue;

                // Duotone ramp: cyan at low power through to ember at full, so power
                // distribution reads at a glance with no legend.
                var t = (bucket + 0.5) / PowerBuckets;
                var pen = new Pen(new SolidColorBrush(RampColour(t)), 1.1 / Zoom);
                context.DrawGeometry(null, pen, geometry);
            }
        }
    }

    private Matrix BuildRenderTransform() =>
        // Geometry is built in bed millimetres; this puts it on screen.
        new(Zoom, 0, 0, -Zoom, Pan.X, Bounds.Height - Pan.Y);

    private static Color RampColour(double t)
    {
        // Cyan (#3FD0E3) → warm neutral → ember (#FF7A3D).
        var (r0, g0, b0) = (0x3F, 0xD0, 0xE3);
        var (r1, g1, b1) = (0xFF, 0x7A, 0x3D);
        byte Mix(int a, int b) => (byte)Math.Clamp(a + (b - a) * t, 0, 255);
        return Color.FromArgb(230, Mix(r0, r1), Mix(g0, g1), Mix(b0, b1));
    }

    private void RebuildToolpathGeometry(Toolpath toolpath)
    {
        _cachedToolpath = toolpath;
        _travelGeometry = null;
        Array.Clear(_burnGeometry);

        var burnContexts = new StreamGeometryContext?[PowerBuckets];
        var geometries = new StreamGeometry[PowerBuckets];
        for (var i = 0; i < PowerBuckets; i++) geometries[i] = new StreamGeometry();

        var travel = new StreamGeometry();
        var travelContext = travel.Open();
        var travelUsed = false;

        var x0 = toolpath.X0;
        var y0 = toolpath.Y0;
        var x1 = toolpath.X1;
        var y1 = toolpath.Y1;
        var power = toolpath.Power;
        var rapid = toolpath.Rapid;

        try
        {
            for (var i = 0; i < toolpath.Count; i++)
            {
                if (rapid[i] || power[i] <= 0)
                {
                    travelContext.BeginFigure(new Point(x0[i], y0[i]), false);
                    travelContext.LineTo(new Point(x1[i], y1[i]));
                    travelContext.EndFigure(false);
                    travelUsed = true;
                    continue;
                }

                var bucket = Math.Clamp((int)(power[i] * PowerBuckets), 0, PowerBuckets - 1);
                burnContexts[bucket] ??= geometries[bucket].Open();

                var ctx = burnContexts[bucket]!;
                ctx.BeginFigure(new Point(x0[i], y0[i]), false);
                ctx.LineTo(new Point(x1[i], y1[i]));
                ctx.EndFigure(false);
            }
        }
        finally
        {
            for (var i = 0; i < PowerBuckets; i++)
            {
                if (burnContexts[i] is null) continue;
                burnContexts[i]!.Dispose();
                _burnGeometry[i] = geometries[i];
            }
            travelContext.Dispose();
        }

        _travelGeometry = travelUsed ? travel : null;
    }

    private void DrawShapes(DrawingContext context)
    {
        var design = Design;
        if (design is null || design.Shapes.Count == 0) return;

        // The document is small compared with a toolpath, so rebuilding on every
        // frame is cheap and avoids an invalidation-tracking bug where an edit does
        // not show up.
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            foreach (var shape in design.Shapes)
            {
                if (!shape.Visible) continue;
                foreach (var path in shape.GetOutlines(0.05))
                {
                    if (path.Count < 2) continue;
                    ctx.BeginFigure(new Point(path[0].X, path[0].Y), false);
                    for (var i = 1; i < path.Count; i++) ctx.LineTo(new Point(path[i].X, path[i].Y));
                    if (path.IsClosed) ctx.LineTo(new Point(path[0].X, path[0].Y));
                    ctx.EndFigure(path.IsClosed);
                }
            }
        }

        using (context.PushTransform(BuildRenderTransform()))
        {
            var hasToolpath = Toolpath is { Count: > 0 };
            var outlineBrush = Brush(Application.Current, hasToolpath ? "InkFaint" : "Ink", Colors.Gray);
            context.DrawGeometry(null, new Pen(outlineBrush, (hasToolpath ? 0.8 : 1.4) / Zoom), geometry);
        }
    }

    private void DrawSelection(DrawingContext context)
    {
        var shape = SelectedShape;
        if (shape is null) return;

        var b = shape.Bounds;
        if (b.IsEmpty) return;

        var topLeft = ToPixels(b.MinX, b.MaxY);
        var bottomRight = ToPixels(b.MaxX, b.MinY);
        var rect = new Rect(topLeft, bottomRight).Inflate(3);

        var accent = Brush(Application.Current, "Selection", Colors.DeepSkyBlue);
        context.DrawRectangle(null, new Pen(accent, 1.25) { DashStyle = new DashStyle([4, 3], 0) }, rect, 4, 4);

        foreach (var corner in new[] { rect.TopLeft, rect.TopRight, rect.BottomLeft, rect.BottomRight })
        {
            context.DrawRectangle(accent, null, new Rect(corner.X - 3.5, corner.Y - 3.5, 7, 7), 2, 2);
        }
    }

    private void DrawHead(DrawingContext context)
    {
        if (HeadPosition is not { } head) return;

        var p = ToPixels(head.X, head.Y);
        var ember = Brush(Application.Current, "Ember", Colors.OrangeRed);

        // Crosshair plus a ring: readable against both the bed and dark artwork.
        context.DrawEllipse(null, new Pen(ember, 1.5), p, 9, 9);
        context.DrawLine(new Pen(ember, 1.5), p - new Vector(14, 0), p - new Vector(4, 0));
        context.DrawLine(new Pen(ember, 1.5), p + new Vector(4, 0), p + new Vector(14, 0));
        context.DrawLine(new Pen(ember, 1.5), p - new Vector(0, 14), p - new Vector(0, 4));
        context.DrawLine(new Pen(ember, 1.5), p + new Vector(0, 4), p + new Vector(0, 14));
        context.DrawEllipse(ember, null, p, 2, 2);
    }

    private static IBrush Brush(Application? app, string key, Color fallback)
    {
        if (app?.TryGetResource(key, app.ActualThemeVariant, out var value) == true && value is IBrush brush)
        {
            return brush;
        }
        return new SolidColorBrush(fallback);
    }
}
