using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OpenBurn.App.Services;
using OpenBurn.Camera;
using OpenBurn.Vision;

namespace OpenBurn.App.Views;

/// <summary>
/// Four-corner bed calibration.
///
/// The whole camera subsystem rests on this one measurement, and it is performed
/// by a person clicking on a photograph, so the window's job is to make clicking
/// accurately easy: the picture is shown as large as it will fit, every click is
/// marked and numbered, and the fit residual is reported the moment the fourth
/// corner lands so a bad click is obvious immediately rather than three jobs later.
/// </summary>
public partial class CalibrationWindow : Window
{
    private readonly CameraFrame _frame;
    private readonly double _bedWidthMm;
    private readonly double _bedHeightMm;
    private readonly List<Point2> _corners = [];

    private Canvas? _canvas;
    private Image? _image;
    private double _scale = 1;

    private static readonly string[] CornerNames = ["rear-left", "rear-right", "front-right", "front-left"];

    public CalibrationWindow() : this(CameraFrame.Create(640, 480, 60), 400, 400)
    {
    }

    public CalibrationWindow(CameraFrame frame, double bedWidthMm, double bedHeightMm)
    {
        _frame = frame;
        _bedWidthMm = bedWidthMm;
        _bedHeightMm = bedHeightMm;

        InitializeComponent();

        _canvas = this.FindControl<Canvas>("ImageCanvas");
        if (_canvas is not null)
        {
            _canvas.PointerPressed += OnCanvasPressed;
            _canvas.SizeChanged += (_, _) => Redraw();
        }

        var slider = this.FindControl<Slider>("LensSlider");
        if (slider is not null) slider.PropertyChanged += OnSliderChanged;

        UpdateStep();
        Redraw();
    }

    /// <summary>The clicked corners, in view order. Null if the operator cancelled.</summary>
    public IReadOnlyList<Point2>? Result { get; private set; }

    /// <summary>The chosen radial correction.</summary>
    public double LensK1 { get; private set; }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnSliderChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != RangeBase_ValueProperty()) return;
        Redraw();
    }

    private static AvaloniaProperty RangeBase_ValueProperty() => Slider.ValueProperty;

    private double CurrentK1 => this.FindControl<Slider>("LensSlider")?.Value ?? 0;

    private void Redraw()
    {
        if (_canvas is null) return;

        _canvas.Children.Clear();

        var corrected = Math.Abs(CurrentK1) < 1e-6
            ? _frame
            : LensModel.Undistort(_frame, new LensParameters(CurrentK1));

        var bitmap = FrameConverter.ToBitmap(corrected);

        var available = _canvas.Bounds;
        if (available.Width < 20 || available.Height < 20) available = new Rect(0, 0, 900, 560);

        _scale = Math.Min(available.Width / _frame.Width, available.Height / _frame.Height);

        _image = new Image
        {
            Source = bitmap,
            Width = _frame.Width * _scale,
            Height = _frame.Height * _scale,
            Stretch = Stretch.Fill,
        };

        Canvas.SetLeft(_image, 0);
        Canvas.SetTop(_image, 0);
        _canvas.Children.Add(_image);

        // Marks and the quadrilateral so far.
        for (var i = 0; i < _corners.Count; i++)
        {
            var p = _corners[i];
            var marker = new Ellipse
            {
                Width = 14,
                Height = 14,
                Stroke = Brushes.OrangeRed,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(90, 255, 122, 61)),
            };
            Canvas.SetLeft(marker, p.X * _scale - 7);
            Canvas.SetTop(marker, p.Y * _scale - 7);
            _canvas.Children.Add(marker);

            var label = new TextBlock
            {
                Text = (i + 1).ToString(),
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                FontSize = 11,
            };
            Canvas.SetLeft(label, p.X * _scale + 10);
            Canvas.SetTop(label, p.Y * _scale - 8);
            _canvas.Children.Add(label);
        }

        if (_corners.Count >= 2)
        {
            for (var i = 0; i < _corners.Count - 1; i++) AddEdge(_corners[i], _corners[i + 1]);
            if (_corners.Count == 4) AddEdge(_corners[3], _corners[0]);
        }

        var lensValue = this.FindControl<TextBlock>("LensValue");
        if (lensValue is not null) lensValue.Text = CurrentK1.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void AddEdge(Point2 a, Point2 b)
    {
        if (_canvas is null) return;
        _canvas.Children.Add(new Line
        {
            StartPoint = new Point(a.X * _scale, a.Y * _scale),
            EndPoint = new Point(b.X * _scale, b.Y * _scale),
            Stroke = Brushes.DeepSkyBlue,
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3],
        });
    }

    private void OnCanvasPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_corners.Count >= 4 || _canvas is null) return;

        var position = e.GetPosition(_canvas);
        if (_scale <= 0) return;

        var imageX = position.X / _scale;
        var imageY = position.Y / _scale;

        if (imageX < 0 || imageY < 0 || imageX > _frame.Width || imageY > _frame.Height) return;

        _corners.Add(new Point2(imageX, imageY));
        UpdateStep();
        Redraw();
    }

    private void UpdateStep()
    {
        var step = this.FindControl<TextBlock>("StepText");
        var apply = this.FindControl<Button>("ApplyButton");
        var residual = this.FindControl<TextBlock>("ResidualText");

        if (_corners.Count < 4)
        {
            if (step is not null) step.Text = $"Click corner {_corners.Count + 1} of 4 — the {CornerNames[_corners.Count]} corner of the bed.";
            if (apply is not null) apply.IsEnabled = false;
            if (residual is not null) residual.Text = string.Empty;
            return;
        }

        var calibration = BedRectifier.Calibrate("preview", "preview", _corners, _bedWidthMm, _bedHeightMm);

        if (step is not null) step.Text = "All four corners placed. Check the outline follows the bed, then apply.";
        if (apply is not null) apply.IsEnabled = true;
        if (residual is not null)
        {
            residual.Text = $"Fit: {calibration.Quality} ({calibration.ResidualPixels:0.##} px)";
        }
    }

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        _corners.Clear();
        UpdateStep();
        Redraw();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_corners.Count != 4) return;
        Result = _corners.ToList();
        LensK1 = CurrentK1;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Result = null; Close(); }
        base.OnKeyDown(e);
    }
}
