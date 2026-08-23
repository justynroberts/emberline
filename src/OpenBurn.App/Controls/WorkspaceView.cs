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
// Control.Arrange(Rect) is a layout method; alias ours so the two never collide.
using ArrangeOps = OpenBurn.Core.Documents.Arrange;

namespace OpenBurn.App.Controls;

/// <summary>
/// The bed, the artwork and the toolpath, on one pan-and-zoom canvas, with direct
/// manipulation of the selection.
///
/// Three things carry the whole control:
///
///  * **Batched geometry.** A raster preview is hundreds of thousands of segments.
///    Drawing each one individually drops the canvas to single figures, so segments
///    are bucketed into eight power bands and each band becomes one
///    <see cref="StreamGeometry"/>, rebuilt only when the toolpath changes.
///  * **Millimetres throughout.** The view transform converts to pixels at the last
///    possible moment, so what the canvas shows and what the machine does are the
///    same numbers.
///  * **Handles in pixels, transforms in millimetres.** A grab handle that shrinks
///    with zoom is unusable, so hit testing is in screen space; everything it
///    produces is in bed space.
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

    /// <summary>Everything currently selected. Owned by the view model; the view only reads it.</summary>
    public static readonly StyledProperty<IReadOnlyList<Shape>?> SelectionProperty =
        AvaloniaProperty.Register<WorkspaceView, IReadOnlyList<Shape>?>(nameof(Selection));

    /// <summary>
    /// A rectified top-down photograph of the bed, drawn behind everything else.
    /// This is what turns positioning from a numeric exercise into a visual one.
    /// </summary>
    public static readonly StyledProperty<Bitmap?> BedImageProperty =
        AvaloniaProperty.Register<WorkspaceView, Bitmap?>(nameof(BedImage));

    public static readonly StyledProperty<double> BedImageOpacityProperty =
        AvaloniaProperty.Register<WorkspaceView, double>(nameof(BedImageOpacity), 0.85);

    /// <summary>
    /// How far through the toolpath the head has got, as a segment index. Segments
    /// before it are drawn bright; the rest are dimmed. Negative disables the split.
    /// </summary>
    public static readonly StyledProperty<int> ProgressSegmentProperty =
        AvaloniaProperty.Register<WorkspaceView, int>(nameof(ProgressSegment), -1);

    /// <summary>Snap increment in millimetres while dragging. Zero disables snapping.</summary>
    public static readonly StyledProperty<int> DocumentVersionProperty =
        AvaloniaProperty.Register<WorkspaceView, int>(nameof(DocumentVersion));

    public static readonly StyledProperty<double> SnapMmProperty =
        AvaloniaProperty.Register<WorkspaceView, double>(nameof(SnapMm), 1.0);

    public static readonly StyledProperty<bool> SnapEnabledProperty =
        AvaloniaProperty.Register<WorkspaceView, bool>(nameof(SnapEnabled), true);

    public MachineProfile? Machine { get => GetValue(MachineProperty); set => SetValue(MachineProperty, value); }
    public Design? Design { get => GetValue(DesignProperty); set => SetValue(DesignProperty, value); }
    public Toolpath? Toolpath { get => GetValue(ToolpathProperty); set => SetValue(ToolpathProperty, value); }
    public Point? HeadPosition { get => GetValue(HeadPositionProperty); set => SetValue(HeadPositionProperty, value); }

    /// <summary>Changes whenever the document does. Drives the shape-geometry cache.</summary>
    public int DocumentVersion { get => GetValue(DocumentVersionProperty); set => SetValue(DocumentVersionProperty, value); }
    public double JobFraction { get => GetValue(JobFractionProperty); set => SetValue(JobFractionProperty, value); }
    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public bool ShowTravel { get => GetValue(ShowTravelProperty); set => SetValue(ShowTravelProperty, value); }
    public IReadOnlyList<Shape>? Selection { get => GetValue(SelectionProperty); set => SetValue(SelectionProperty, value); }
    public Bitmap? BedImage { get => GetValue(BedImageProperty); set => SetValue(BedImageProperty, value); }
    public double BedImageOpacity { get => GetValue(BedImageOpacityProperty); set => SetValue(BedImageOpacityProperty, value); }
    public double SnapMm { get => GetValue(SnapMmProperty); set => SetValue(SnapMmProperty, value); }
    public int ProgressSegment { get => GetValue(ProgressSegmentProperty); set => SetValue(ProgressSegmentProperty, value); }
    public bool SnapEnabled { get => GetValue(SnapEnabledProperty); set => SetValue(SnapEnabledProperty, value); }

    /// <summary>Pixels per millimetre.</summary>
    public double Zoom { get; private set; } = 2.0;

    /// <summary>Pan offset in pixels.</summary>
    public Vector Pan { get; private set; }

    /// <summary>Cursor position in bed millimetres, for the coordinate readout.</summary>
    public event Action<Vec2>? CursorMoved;

    /// <summary>The user picked shapes. An empty list means they clicked empty space.</summary>
    public event Action<IReadOnlyList<Shape>, bool>? SelectionRequested;

    /// <summary>A drag is about to modify the selection — the view model should snapshot for undo.</summary>
    public event Action<string>? EditBegan;

    /// <summary>The drag finished. Fires once, after the last change.</summary>
    public event Action? EditEnded;

    /// <summary>Fired continuously during a drag so the preview can regenerate.</summary>
    public event Action? EditChanged;

    /// <summary>Double-click on the bed, in millimetres — used for "move the head here".</summary>
    public event Action<Vec2>? BedDoubleClicked;

    private const int PowerBuckets = 8;
    private readonly StreamGeometry?[] _burnGeometry = new StreamGeometry?[PowerBuckets];
    private StreamGeometry? _travelGeometry;
    private Toolpath? _cachedToolpath;

    // The "already cut" overlay, rebuilt as playback advances.
    private StreamGeometry? _completedGeometry;
    private int _completedUpTo = -1;

    /// <summary>
    /// Above this many segments the progress overlay is skipped and only the head
    /// marker moves. Rebuilding a geometry of half a million segments every frame
    /// would cost more than the information is worth, and the head is the part
    /// people actually watch.
    /// </summary>
    private const int MaxSegmentsForProgressOverlay = 60_000;

    private bool _panning;
    private Point _panStart;
    private Vector _panOrigin;
    private bool _hasFitted;

    // Drag state
    private HandleKind _activeHandle = HandleKind.None;
    private Vec2 _dragStartMm;
    private Vec2 _dragAnchor;
    private Rect2 _dragStartBounds;
    private Vec2 _lastAppliedMm;
    private double _accumulatedRotation;
    private bool _editing;

    // Marquee
    private bool _marquee;
    private Point _marqueeStart;
    private Point _marqueeCurrent;

    static WorkspaceView()
    {
        AffectsRender<WorkspaceView>(MachineProperty, DesignProperty, ToolpathProperty, HeadPositionProperty,
            DocumentVersionProperty,
                                     JobFractionProperty, ShowGridProperty, ShowTravelProperty, SelectionProperty,
                                     BedImageProperty, BedImageOpacityProperty, ProgressSegmentProperty);
    }

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

    /// <summary>Bed millimetres to control pixels. Y is flipped: the bed grows up, the screen grows down.</summary>
    public Point ToPixels(double xMm, double yMm) =>
        new(xMm * Zoom + Pan.X, Bounds.Height - (yMm * Zoom + Pan.Y));

    public Point ToPixels(Vec2 mm) => ToPixels(mm.X, mm.Y);

    public Vec2 ToMillimetres(Point pixel) =>
        new((pixel.X - Pan.X) / Zoom, (Bounds.Height - pixel.Y - Pan.Y) / Zoom);

    /// <summary>
    /// Inset of the floating chrome — tool rail, inspector, status bar — so a fit
    /// centres the bed in the part of the canvas the operator can actually see
    /// rather than in the part hidden behind a panel.
    /// </summary>
    public Thickness ChromeInset { get; set; } = new(90, 100, 356, 56);

    public void ZoomToFitBed()
    {
        var machine = Machine;
        if (machine is null) return;
        FitTo(0, 0, machine.BedWidthMm, machine.BedHeightMm);
    }

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

    private void FitTo(double minX, double minY, double width, double height)
    {
        if (Bounds.Width < 10 || Bounds.Height < 10 || width <= 0 || height <= 0) return;

        const double margin = 28;
        var usableWidth = Bounds.Width - ChromeInset.Left - ChromeInset.Right - margin * 2;
        var usableHeight = Bounds.Height - ChromeInset.Top - ChromeInset.Bottom - margin * 2;
        if (usableWidth < 40 || usableHeight < 40) return;

        Zoom = Math.Clamp(Math.Min(usableWidth / width, usableHeight / height), 0.05, 80);

        var centreX = ChromeInset.Left + margin + usableWidth / 2;
        var centreY = ChromeInset.Top + margin + usableHeight / 2;

        Pan = new Vector(
            centreX - (minX + width / 2) * Zoom,
            Bounds.Height - centreY - (minY + height / 2) * Zoom);

        InvalidateVisual();
    }

    /// <summary>Zoom about a pivot, keeping whatever is under it stationary.</summary>
    public void ZoomBy(double factor, Point? centre = null)
    {
        var pivot = centre ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        var before = ToMillimetres(pivot);

        Zoom = Math.Clamp(Zoom * factor, 0.05, 80);

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

        var wantsPan = point.Properties.IsMiddleButtonPressed ||
                       point.Properties.IsRightButtonPressed ||
                       e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (wantsPan)
        {
            _panning = true;
            _panStart = position;
            _panOrigin = Pan;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            BedDoubleClicked?.Invoke(ToMillimetres(position));
            e.Handled = true;
            return;
        }

        // A handle on the current selection takes priority over picking something new.
        var selectionBounds = SelectionBounds();
        if (!selectionBounds.IsEmpty)
        {
            var handle = SelectionInteraction.HitTest(ToPixelRect(selectionBounds), position);
            if (handle is not HandleKind.None and not HandleKind.Move)
            {
                BeginDrag(handle, position, selectionBounds);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var hit = HitTest(ToMillimetres(position));

        if (hit is null)
        {
            // Empty space: start a marquee rather than clearing immediately, so a
            // drag-select does not flash the selection off first.
            _marquee = true;
            _marqueeStart = position;
            _marqueeCurrent = position;
            if (!additive) SelectionRequested?.Invoke([], false);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var alreadySelected = Selection?.Contains(hit) == true;
        if (!alreadySelected || additive) SelectionRequested?.Invoke([hit], additive);

        // Dragging the body moves whatever is selected after the click resolves.
        if (!hit.Locked)
        {
            BeginDrag(HandleKind.Move, position, SelectionBounds(hit));
            e.Pointer.Capture(this);
        }

        e.Handled = true;
    }

    private void BeginDrag(HandleKind handle, Point position, Rect2 bounds)
    {
        _activeHandle = handle;
        _dragStartMm = ToMillimetres(position);
        _lastAppliedMm = _dragStartMm;
        _dragStartBounds = bounds;
        _dragAnchor = SelectionInteraction.AnchorFor(handle, bounds);
        _accumulatedRotation = 0;
        _editing = false;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var position = e.GetPosition(this);
        var mm = ToMillimetres(position);
        CursorMoved?.Invoke(mm);

        if (_panning)
        {
            var delta = position - _panStart;
            Pan = new Vector(_panOrigin.X + delta.X, _panOrigin.Y - delta.Y);
            InvalidateVisual();
            return;
        }

        if (_marquee)
        {
            _marqueeCurrent = position;
            InvalidateVisual();
            return;
        }

        if (_activeHandle != HandleKind.None)
        {
            ApplyDrag(mm, e.KeyModifiers);
            return;
        }

        // Cursor feedback so the handles are discoverable without a tooltip.
        var bounds = SelectionBounds();
        Cursor = bounds.IsEmpty
            ? Cursor.Default
            : new Cursor(SelectionInteraction.CursorFor(SelectionInteraction.HitTest(ToPixelRect(bounds), position)));
    }

    private void ApplyDrag(Vec2 mm, KeyModifiers modifiers)
    {
        var targets = SelectionOrEmpty().Where(s => !s.Locked).ToList();
        if (targets.Count == 0) return;

        if (!_editing)
        {
            _editing = true;
            EditBegan?.Invoke(_activeHandle switch
            {
                HandleKind.Move => "Move",
                HandleKind.Rotate => "Rotate",
                _ => "Resize",
            });
        }

        switch (_activeHandle)
        {
            case HandleKind.Move:
            {
                var target = mm;
                if (SnapEnabled && !modifiers.HasFlag(KeyModifiers.Control))
                {
                    // Snap the *bounding box corner* rather than the cursor, so a
                    // shape lands on the grid rather than the pointer doing so.
                    var offset = new Vec2(_dragStartBounds.MinX - _dragStartMm.X, _dragStartBounds.MinY - _dragStartMm.Y);
                    var corner = SelectionInteraction.SnapToGrid(new Vec2(mm.X + offset.X, mm.Y + offset.Y), SnapMm);
                    target = new Vec2(corner.X - offset.X, corner.Y - offset.Y);
                }

                var delta = new Vec2(target.X - _lastAppliedMm.X, target.Y - _lastAppliedMm.Y);
                if (delta.LengthSquared < 1e-12) return;

                foreach (var shape in targets) shape.Translate(delta);
                _lastAppliedMm = target;
                break;
            }

            case HandleKind.Rotate:
            {
                var pivot = _dragStartBounds.Center;
                var angle = SelectionInteraction.AngleBetween(pivot, _dragStartMm, mm);
                if (modifiers.HasFlag(KeyModifiers.Shift)) angle = SelectionInteraction.SnapAngle(angle);

                var delta = angle - _accumulatedRotation;
                if (Math.Abs(delta) < 1e-6) return;

                ArrangeOps.RotateSelection(targets, delta, pivot);
                _accumulatedRotation = angle;
                break;
            }

            default:
            {
                // Scaling is computed against the *original* bounds each time, so
                // the shape follows the pointer exactly instead of drifting as
                // rounding accumulates over hundreds of incremental factors.
                var current = SelectionBounds();
                if (current.IsEmpty || _dragStartBounds.IsEmpty) return;

                var uniform = modifiers.HasFlag(KeyModifiers.Shift) ||
                              _activeHandle is HandleKind.ScaleTopLeft or HandleKind.ScaleTopRight
                                  or HandleKind.ScaleBottomLeft or HandleKind.ScaleBottomRight &&
                              modifiers.HasFlag(KeyModifiers.Shift);

                var (targetX, targetY) = SelectionInteraction.ScaleFactors(_activeHandle, _dragStartBounds, _dragAnchor, mm, uniform);

                var appliedX = _dragStartBounds.Width > 1e-9 ? current.Width / _dragStartBounds.Width : 1;
                var appliedY = _dragStartBounds.Height > 1e-9 ? current.Height / _dragStartBounds.Height : 1;

                var stepX = appliedX > 1e-9 ? targetX / appliedX : 1;
                var stepY = appliedY > 1e-9 ? targetY / appliedY : 1;

                if (Math.Abs(stepX - 1) < 1e-9 && Math.Abs(stepY - 1) < 1e-9) return;

                ArrangeOps.ScaleSelection(targets, stepX, stepY, _dragAnchor);
                break;
            }
        }

        _shapeGeometry = null;
        EditChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            e.Pointer.Capture(null);
            return;
        }

        if (_marquee)
        {
            _marquee = false;
            e.Pointer.Capture(null);

            var a = ToMillimetres(_marqueeStart);
            var b = ToMillimetres(_marqueeCurrent);
            var rect = Rect2.FromPoints([a, b]);

            // A tiny marquee is a click on empty space, not a selection attempt.
            if (rect.Width > 0.5 || rect.Height > 0.5)
            {
                // Dragging right-to-left selects anything touched, left-to-right only
                // what is fully enclosed — the convention every CAD package uses.
                var requireInside = _marqueeCurrent.X >= _marqueeStart.X;
                var found = SelectionInteraction.InMarquee(Design?.Shapes ?? [], rect, requireInside);
                SelectionRequested?.Invoke(found, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            }

            InvalidateVisual();
            return;
        }

        if (_activeHandle != HandleKind.None)
        {
            _activeHandle = HandleKind.None;
            e.Pointer.Capture(null);

            if (_editing)
            {
                _editing = false;
                EditEnded?.Invoke();
            }
        }
    }

    private Shape? HitTest(Vec2 mm)
    {
        var design = Design;
        if (design is null) return null;

        // Topmost first, with a tolerance that scales with zoom so small shapes
        // stay clickable when zoomed out.
        var tolerance = 4 / Math.Max(Zoom, 0.01);
        for (var i = design.Shapes.Count - 1; i >= 0; i--)
        {
            var shape = design.Shapes[i];
            if (!shape.Visible) continue;
            if (shape.Bounds.Inflate(tolerance).Contains(mm)) return shape;
        }
        return null;
    }

    private IReadOnlyList<Shape> SelectionOrEmpty() => Selection ?? [];

    private Rect2 SelectionBounds(Shape? include = null)
    {
        var r = Rect2.Empty;
        foreach (var s in SelectionOrEmpty()) r = r.Union(s.Bounds);
        if (include is not null) r = r.Union(include.Bounds);
        return r;
    }

    private Rect ToPixelRect(Rect2 bounds)
    {
        var topLeft = ToPixels(bounds.MinX, bounds.MaxY);
        var bottomRight = ToPixels(bounds.MaxX, bounds.MinY);
        return new Rect(topLeft, bottomRight);
    }

    // --------------------------------------------------------------- render

    public override void Render(DrawingContext context)
    {
        var machine = Machine;
        context.FillRectangle(Brush("BgSunken", Colors.Black), new Rect(Bounds.Size));

        if (machine is null) return;

        DrawBed(context, machine);
        DrawBedImage(context, machine);
        if (ShowGrid) DrawGrid(context, machine);
        DrawShapes(context);
        DrawToolpath(context);
        DrawSelection(context);
        DrawMarquee(context);
        DrawOrigin(context);
        DrawHead(context);
    }

    private void DrawBed(DrawingContext context, MachineProfile machine)
    {
        var rect = ToPixelRect(new Rect2(0, 0, machine.BedWidthMm, machine.BedHeightMm));
        context.DrawRectangle(Brush("BedFill", Colors.White), new Pen(Brush("BedEdge", Colors.Gray), 1.25), rect, 8, 8);
    }

    /// <summary>
    /// Draw the camera view stretched across the bed. The image is already
    /// rectified and scaled to the bed, so this is a straight blit into the bed
    /// rectangle — no transform guesswork at draw time.
    /// </summary>
    private void DrawBedImage(DrawingContext context, MachineProfile machine)
    {
        if (BedImage is not { } bitmap) return;

        var target = ToPixelRect(new Rect2(0, 0, machine.BedWidthMm, machine.BedHeightMm));
        using (context.PushOpacity(Math.Clamp(BedImageOpacity, 0, 1)))
        {
            context.DrawImage(bitmap, new Rect(bitmap.Size), target);
        }
    }

    private void DrawGrid(DrawingContext context, MachineProfile machine)
    {
        var minorPen = new Pen(Brush("GridMinor", Colors.Gray), 1);
        var majorPen = new Pen(Brush("GridMajor", Colors.Gray), 1);
        var showMinor = Zoom > 1.4;

        for (double x = 0; x <= machine.BedWidthMm + 0.01; x += 10)
        {
            var major = Math.Abs(x % 50) < 0.01;
            if (!major && !showMinor) continue;
            context.DrawLine(major ? majorPen : minorPen, ToPixels(x, 0), ToPixels(x, machine.BedHeightMm));
        }

        for (double y = 0; y <= machine.BedHeightMm + 0.01; y += 10)
        {
            var major = Math.Abs(y % 50) < 0.01;
            if (!major && !showMinor) continue;
            context.DrawLine(major ? majorPen : minorPen, ToPixels(0, y), ToPixels(machine.BedWidthMm, y));
        }
    }

    private void DrawOrigin(DrawingContext context)
    {
        var origin = ToPixels(0, 0);
        var ember = Brush("Ember", Colors.OrangeRed);
        var cyan = Brush("Cyan", Colors.Cyan);

        context.DrawLine(new Pen(ember, 2), origin, origin + new Vector(18, 0));
        context.DrawLine(new Pen(cyan, 2), origin, origin - new Vector(0, 18));
        context.DrawEllipse(ember, null, origin, 3, 3);
    }

    private void DrawToolpath(DrawingContext context)
    {
        var toolpath = Toolpath;
        if (toolpath is null || toolpath.Count == 0) return;

        if (!ReferenceEquals(_cachedToolpath, toolpath)) RebuildToolpathGeometry(toolpath);

        var transform = BuildRenderTransform();

        if (ShowTravel && _travelGeometry is not null)
        {
            var pen = new Pen(Brush("Travel", Colors.Teal), 0.75) { DashStyle = new DashStyle([3, 3], 0) };
            using (context.PushTransform(transform)) context.DrawGeometry(null, pen, _travelGeometry);
        }

        var progress = ProgressSegment;
        var showingProgress = progress >= 0 && toolpath.Count <= MaxSegmentsForProgressOverlay;

        using (context.PushTransform(transform))
        using (context.PushOpacity(showingProgress ? 0.3 : 1.0))
        {
            for (var bucket = 0; bucket < PowerBuckets; bucket++)
            {
                var geometry = _burnGeometry[bucket];
                if (geometry is null) continue;

                // Duotone ramp: cyan at low power through to ember at full, so power
                // distribution reads at a glance with no legend.
                var t = (bucket + 0.5) / PowerBuckets;
                context.DrawGeometry(null, new Pen(new SolidColorBrush(RampColour(t)), 1.1 / Zoom), geometry);
            }
        }

        if (!showingProgress) return;

        if (_completedUpTo != progress) RebuildCompletedGeometry(toolpath, progress);

        if (_completedGeometry is not null)
        {
            using (context.PushTransform(transform))
            {
                context.DrawGeometry(null, new Pen(Brush("Ember", Colors.OrangeRed), 1.6 / Zoom), _completedGeometry);
            }
        }
    }

    /// <summary>Geometry for everything burned so far, so the operator can see how far in they are.</summary>
    private void RebuildCompletedGeometry(Toolpath toolpath, int upTo)
    {
        _completedUpTo = upTo;

        var limit = Math.Clamp(upTo, 0, toolpath.Count);
        if (limit == 0)
        {
            _completedGeometry = null;
            return;
        }

        var geometry = new StreamGeometry();
        var used = false;

        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < limit; i++)
            {
                if (toolpath.Rapid[i] || toolpath.Power[i] <= 0) continue;
                ctx.BeginFigure(new Point(toolpath.X0[i], toolpath.Y0[i]), false);
                ctx.LineTo(new Point(toolpath.X1[i], toolpath.Y1[i]));
                ctx.EndFigure(false);
                used = true;
            }
        }

        _completedGeometry = used ? geometry : null;
    }

    /// <summary>Geometry is built in bed millimetres; this puts it on screen.</summary>
    private Matrix BuildRenderTransform() => new(Zoom, 0, 0, -Zoom, Pan.X, Bounds.Height - Pan.Y);

    private static Color RampColour(double t)
    {
        var (r0, g0, b0) = (0x3F, 0xD0, 0xE3);
        var (r1, g1, b1) = (0xFF, 0x7A, 0x3D);
        byte Mix(int a, int b) => (byte)Math.Clamp(a + (b - a) * t, 0, 255);
        return Color.FromArgb(230, Mix(r0, r1), Mix(g0, g1), Mix(b0, b1));
    }

    private void RebuildToolpathGeometry(Toolpath toolpath)
    {
        _cachedToolpath = toolpath;
        _travelGeometry = null;
        _completedGeometry = null;
        _completedUpTo = -1;
        Array.Clear(_burnGeometry);

        var contexts = new StreamGeometryContext?[PowerBuckets];
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
                contexts[bucket] ??= geometries[bucket].Open();

                var ctx = contexts[bucket]!;
                ctx.BeginFigure(new Point(x0[i], y0[i]), false);
                ctx.LineTo(new Point(x1[i], y1[i]));
                ctx.EndFigure(false);
            }
        }
        finally
        {
            for (var i = 0; i < PowerBuckets; i++)
            {
                if (contexts[i] is null) continue;
                contexts[i]!.Dispose();
                _burnGeometry[i] = geometries[i];
            }
            travelContext.Dispose();
        }

        _travelGeometry = travelUsed ? travel : null;
    }

    private StreamGeometry? _shapeGeometry;
    private object? _geometrySource;
    private int _geometryVersion = -1;

    /// <summary>
    /// How many times the shape geometry has been rebuilt. Diagnostic: a cache
    /// that silently stops working looks exactly like one that works, so this is
    /// what the tests assert against.
    /// </summary>
    public int GeometryRebuilds { get; private set; }

    /// <summary>
    /// The shape outlines, in millimetres, cached until the document changes.
    ///
    /// This used to be rebuilt every frame on the grounds that a document is small
    /// next to a toolpath. That holds for a handful of imported curves and breaks
    /// completely for a traced bitmap: a quarter of a million points means
    /// GetOutlines allocating a transformed copy of every polyline, several times
    /// over per frame once selection and hit-testing have had their turn, and
    /// dragging turns to treacle.
    ///
    /// Pan and zoom do not invalidate it — the transform is applied at draw time,
    /// so the geometry itself only depends on the document.
    /// </summary>
    private StreamGeometry BuildShapeGeometry(Design design)
    {
        if (_shapeGeometry is not null &&
            ReferenceEquals(_geometrySource, design) &&
            _geometryVersion == DocumentVersion)
        {
            return _shapeGeometry;
        }

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

        _shapeGeometry = geometry;
        _geometrySource = design;
        _geometryVersion = DocumentVersion;
        GeometryRebuilds++;
        return geometry;
    }

    /// <summary>Drop the cached geometry — for edits made on the canvas itself.</summary>
    public void InvalidateShapes()
    {
        _shapeGeometry = null;
        InvalidateVisual();
    }

    private void DrawShapes(DrawingContext context)
    {
        var design = Design;
        if (design is null || design.Shapes.Count == 0) return;

        var geometry = BuildShapeGeometry(design);

        using (context.PushTransform(BuildRenderTransform()))
        {
            var hasToolpath = Toolpath is { Count: > 0 };
            var outlineBrush = Brush(hasToolpath ? "InkFaint" : "Ink", Colors.Gray);
            context.DrawGeometry(null, new Pen(outlineBrush, (hasToolpath ? 0.8 : 1.4) / Zoom), geometry);
        }
    }

    private void DrawSelection(DrawingContext context)
    {
        var selection = SelectionOrEmpty();
        if (selection.Count == 0) return;

        var accent = Brush("Selection", Colors.DeepSkyBlue);

        // Each shape gets a light outline; the group gets the handles.
        if (selection.Count > 1)
        {
            foreach (var shape in selection)
            {
                var b = shape.Bounds;
                if (b.IsEmpty) continue;
                using (context.PushOpacity(0.5))
                {
                    context.DrawRectangle(null, new Pen(accent, 1) { DashStyle = new DashStyle([3, 3], 0) },
                                          ToPixelRect(b), 2, 2);
                }
            }
        }

        var bounds = SelectionBounds();
        if (bounds.IsEmpty) return;

        var rect = ToPixelRect(bounds).Inflate(3);
        context.DrawRectangle(null, new Pen(accent, 1.25) { DashStyle = new DashStyle([4, 3], 0) }, rect, 4, 4);

        var locked = selection.All(s => s.Locked);
        if (locked) return;

        // Rotate handle, joined to the box so it reads as belonging to it.
        var rotateAt = new Point(rect.Center.X, rect.Top - SelectionInteraction.RotateHandleOffset);
        context.DrawLine(new Pen(accent, 1) { DashStyle = new DashStyle([2, 2], 0) },
                         new Point(rect.Center.X, rect.Top), rotateAt);
        context.DrawEllipse(Brush("PanelSolid", Colors.White), new Pen(accent, 1.5), rotateAt, 5, 5);

        var half = SelectionInteraction.HandleSize / 2;
        foreach (var handle in new[]
                 {
                     rect.TopLeft, rect.TopRight, rect.BottomLeft, rect.BottomRight,
                     new Point(rect.Center.X, rect.Top), new Point(rect.Center.X, rect.Bottom),
                     new Point(rect.Left, rect.Center.Y), new Point(rect.Right, rect.Center.Y),
                 })
        {
            context.DrawRectangle(
                Brush("PanelSolid", Colors.White),
                new Pen(accent, 1.5),
                new Rect(handle.X - half, handle.Y - half, SelectionInteraction.HandleSize, SelectionInteraction.HandleSize),
                2, 2);
        }
    }

    private void DrawMarquee(DrawingContext context)
    {
        if (!_marquee) return;

        var rect = new Rect(_marqueeStart, _marqueeCurrent);
        if (rect.Width < 1 && rect.Height < 1) return;

        var accent = Brush("Selection", Colors.DeepSkyBlue);
        using (context.PushOpacity(0.14)) context.FillRectangle(accent, rect);
        context.DrawRectangle(null, new Pen(accent, 1) { DashStyle = new DashStyle([4, 3], 0) }, rect, 2, 2);
    }

    private void DrawHead(DrawingContext context)
    {
        if (HeadPosition is not { } head) return;

        var p = ToPixels(head.X, head.Y);
        var ember = Brush("Ember", Colors.OrangeRed);

        context.DrawEllipse(null, new Pen(ember, 1.5), p, 9, 9);
        context.DrawLine(new Pen(ember, 1.5), p - new Vector(14, 0), p - new Vector(4, 0));
        context.DrawLine(new Pen(ember, 1.5), p + new Vector(4, 0), p + new Vector(14, 0));
        context.DrawLine(new Pen(ember, 1.5), p - new Vector(0, 14), p - new Vector(0, 4));
        context.DrawLine(new Pen(ember, 1.5), p + new Vector(0, 4), p + new Vector(0, 14));
        context.DrawEllipse(ember, null, p, 2, 2);
    }

    private static IBrush Brush(string key, Color fallback)
    {
        var app = Application.Current;
        if (app?.TryGetResource(key, app.ActualThemeVariant, out var value) == true && value is IBrush brush) return brush;
        return new SolidColorBrush(fallback);
    }
}
