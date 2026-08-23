using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenBurn.App.Services;
using OpenBurn.Camera;
using OpenBurn.Vision;

namespace OpenBurn.App.ViewModels;

/// <summary>
/// The camera subsystem, from the shell's point of view.
///
/// The workflow this exists to serve, straight from the PRD: put material on the
/// bed, capture it, see it on the canvas, drag artwork onto it, frame, start.
/// Everything below — undistortion, perspective correction, scaling to millimetres
/// — happens once here and never has to be thought about again.
/// </summary>
public sealed partial class MainViewModel
{
    private ICameraSource? _camera;
    private CameraFrame? _lastRawFrame;
    private BedRectifier? _rectifier;

    public ObservableCollection<CameraDescriptor> Cameras { get; } = [];

    [ObservableProperty]
    private CameraDescriptor? _selectedCamera;

    [ObservableProperty]
    private string _cameraAddress = string.Empty;

    [ObservableProperty]
    private Bitmap? _bedImage;

    [ObservableProperty]
    private bool _showBedImage = true;

    [ObservableProperty]
    private double _bedImageOpacity = 0.85;

    [ObservableProperty]
    private string? _cameraStatus;

    [ObservableProperty]
    private bool _isCameraLive;

    public CameraCalibration? Calibration { get; private set; }

    public bool IsCalibrated => Calibration is { IsUsable: true };

    public string CalibrationSummary => Calibration is { IsUsable: true } c
        ? $"Calibrated {c.CalibratedAt.ToLocalTime():d MMM HH:mm} · {c.Quality}"
        : "Not calibrated for this machine and camera";

    /// <summary>The raw frame, for the calibration window to click corners on.</summary>
    public CameraFrame? LastRawFrame => _lastRawFrame;

    [RelayCommand]
    private void RefreshCameras()
    {
        Cameras.Clear();

        // The synthetic source is always available, which means the whole camera
        // workflow can be learned — and demonstrated — with no hardware at all.
        Cameras.Add(new CameraDescriptor("synthetic", "Synthetic bed camera (demo)", CameraKind.Synthetic));

        if (!string.IsNullOrWhiteSpace(CameraAddress))
        {
            var address = CameraAddress.Trim();
            var kind = address.Contains("mjpg", StringComparison.OrdinalIgnoreCase) ||
                       address.Contains("mjpeg", StringComparison.OrdinalIgnoreCase) ||
                       address.Contains("stream", StringComparison.OrdinalIgnoreCase)
                ? CameraKind.Mjpeg
                : CameraKind.Snapshot;

            Cameras.Add(new CameraDescriptor(address, kind == CameraKind.Mjpeg ? "MJPEG camera" : "Snapshot camera", kind, address));
        }

        SelectedCamera ??= Cameras.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ConnectCameraAsync()
    {
        await DisconnectCameraAsync().ConfigureAwait(true);

        var descriptor = SelectedCamera;
        if (descriptor is null)
        {
            RefreshCameras();
            descriptor = SelectedCamera;
            if (descriptor is null) return;
        }

        try
        {
            _camera = descriptor.Kind switch
            {
                CameraKind.Synthetic => new SyntheticCameraSource(800, 600),
                CameraKind.Mjpeg => new MjpegCameraSource(descriptor.Address!),
                CameraKind.Snapshot => new SnapshotCameraSource(descriptor.Address!),
                CameraKind.File => new FileCameraSource(descriptor.Address!),
                _ => throw new NotSupportedException(
                    $"{descriptor.Kind} cameras are not supported yet. Use an MJPEG or snapshot URL, " +
                    "or the synthetic camera to try the workflow."),
            };

            _camera.FrameReceived += OnFrame;
            _camera.Failed += ex => Dispatcher.UIThread.Post(() => CameraStatus = $"Camera error: {ex.Message}");

            await _camera.StartAsync().ConfigureAwait(true);

            IsCameraLive = true;
            CameraStatus = $"Connected to {descriptor.Name}.";
            LoadCalibration();
        }
        catch (Exception ex)
        {
            CameraStatus = ex.Message;
            _camera = null;
            IsCameraLive = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectCameraAsync()
    {
        if (_camera is null) return;

        _camera.FrameReceived -= OnFrame;
        await _camera.DisposeAsync().ConfigureAwait(true);
        _camera = null;
        IsCameraLive = false;
        CameraStatus = null;
    }

    private void OnFrame(CameraFrame frame)
    {
        _lastRawFrame = frame;

        // Only the rectified view goes on the canvas; a raw frame would be the
        // wrong shape and would place artwork wrongly, which is worse than no
        // camera at all.
        if (_rectifier is null || !ShowBedImage) return;

        try
        {
            var rectified = _rectifier.Rectify(frame, pixelsPerMm: 2.0);
            Dispatcher.UIThread.Post(() => BedImage = FrameConverter.ToBitmap(rectified));
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => CameraStatus = $"Could not rectify the frame: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CaptureBedAsync()
    {
        if (_camera is null)
        {
            await ConnectCameraAsync().ConfigureAwait(true);
            if (_camera is null) return;
        }

        var frame = await _camera.CaptureAsync().ConfigureAwait(true);
        if (frame is null)
        {
            CameraStatus = "No frame arrived from the camera.";
            return;
        }

        _lastRawFrame = frame;

        if (_rectifier is null)
        {
            CameraStatus = "Captured, but this camera is not calibrated yet — calibrate to place it on the bed.";
            return;
        }

        BedImage = FrameConverter.ToBitmap(_rectifier.Rectify(frame, pixelsPerMm: 2.0));
        CameraStatus = "Bed captured.";
    }

    /// <summary>
    /// Calibrate from four bed corners clicked on the raw camera image, in view
    /// order: top-left, top-right, bottom-right, bottom-left.
    /// </summary>
    public void ApplyCalibration(IReadOnlyList<Point2> imageCorners, double lensK1)
    {
        try
        {
            var calibration = BedRectifier.Calibrate(
                SelectedMachine.Id,
                SelectedCamera?.Id ?? "camera",
                imageCorners,
                SelectedMachine.BedWidthMm,
                SelectedMachine.BedHeightMm,
                Math.Abs(lensK1) < 1e-6 ? LensParameters.None : new LensParameters(lensK1));

            calibration.Save();
            Calibration = calibration;
            _rectifier = new BedRectifier(calibration);

            OnPropertyChanged(nameof(IsCalibrated));
            OnPropertyChanged(nameof(CalibrationSummary));

            CameraStatus = $"Calibrated. Fit quality: {calibration.Quality} ({calibration.ResidualPixels:0.##} px residual).";
            Console.AppendInfo($"Camera calibrated for {SelectedMachine.DisplayName}: {calibration.Quality}.");

            if (_lastRawFrame is not null) BedImage = FrameConverter.ToBitmap(_rectifier.Rectify(_lastRawFrame, 2.0));
        }
        catch (Exception ex)
        {
            CameraStatus = $"Calibration failed: {ex.Message}";
        }
    }

    private void LoadCalibration()
    {
        Calibration = CameraCalibration.Load(SelectedMachine.Id, SelectedCamera?.Id ?? "camera");
        _rectifier = Calibration is { IsUsable: true } c ? new BedRectifier(c) : null;

        OnPropertyChanged(nameof(IsCalibrated));
        OnPropertyChanged(nameof(CalibrationSummary));

        if (_rectifier is null)
        {
            CameraStatus = "This camera has no calibration for this machine yet. Use Calibrate to set one up.";
        }
    }

    [RelayCommand]
    private void ClearBedImage()
    {
        BedImage = null;
        CameraStatus = null;
    }

    partial void OnShowBedImageChanged(bool value)
    {
        if (!value) BedImage = null;
        else if (_lastRawFrame is not null && _rectifier is not null)
        {
            BedImage = FrameConverter.ToBitmap(_rectifier.Rectify(_lastRawFrame, 2.0));
        }
    }

    /// <summary>
    /// Find distinct workpieces in the captured bed image and report them.
    ///
    /// The "four coasters" case from the PRD: detect them, then the operator can
    /// centre the artwork on one and duplicate onto the rest.
    /// </summary>
    [RelayCommand]
    private void DetectWorkpieces()
    {
        if (_rectifier is null || _lastRawFrame is null)
        {
            CameraStatus = "Capture and calibrate the bed first.";
            return;
        }

        const double pixelsPerMm = 2.0;
        var rectified = _rectifier.Rectify(_lastRawFrame, pixelsPerMm);
        var found = WorkpieceDetector.Detect(rectified, pixelsPerMm, minimumSizeMm: 10);

        if (found.Count == 0)
        {
            CameraStatus = "No distinct workpieces found. Try better lighting, or a background with more contrast.";
            return;
        }

        CameraStatus = $"Found {found.Count} workpiece(s).";
        foreach (var workpiece in found) Console.AppendInfo($"Workpiece: {workpiece.Describe()}");

        // Centre the selection on the largest one, which is the common case.
        if (SelectedShape is { } shape)
        {
            var target = found[0];
            var bounds = shape.Bounds;
            shape.MoveTo(new Core.Geometry.Vec2(
                target.CentreXMm - bounds.Width / 2,
                target.CentreYMm - bounds.Height / 2));

            QueueRegenerate();
            Console.AppendInfo($"Moved '{shape.Name}' onto the largest workpiece.");
        }
    }
}
