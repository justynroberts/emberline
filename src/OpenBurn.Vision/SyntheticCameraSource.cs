using OpenBurn.Camera;

namespace OpenBurn.Vision;

/// <summary>
/// A generated bed view.
///
/// It lives in the vision assembly rather than the camera one because it needs the
/// perspective and lens maths to produce a realistic image, and OpenBurn.Camera is
/// deliberately kept free of that dependency so a plain HTTP camera pulls in
/// nothing it does not need.
///
/// The scene is: a chequerboard ground, a bright rectangular workpiece,
/// and four dark corner fiducials — rendered through a deliberate perspective
/// warp and barrel distortion.
///
/// This is the camera equivalent of the virtual laser. It means the entire camera
/// path — calibration, undistortion, perspective correction, fiducial detection,
/// object detection — can be exercised in CI with known-correct ground truth,
/// which is the only way to test computer vision without a rig on a desk.
/// </summary>
public sealed class SyntheticCameraSource : CameraSourceBase
{
    private readonly int _width;
    private readonly int _height;
    private CancellationTokenSource? _loop;

    public SyntheticCameraSource(int width = 640, int height = 480, SyntheticSceneOptions? options = null)
    {
        _width = width;
        _height = height;
        Options = options ?? SyntheticSceneOptions.Default;
        Descriptor = new CameraDescriptor("synthetic", "Synthetic bed camera", CameraKind.Synthetic);
    }

    public SyntheticSceneOptions Options { get; set; }

    public override CameraDescriptor Descriptor { get; }

    /// <summary>The four bed corners as they land in the image, in view order: TL, TR, BR, BL.</summary>
    public (double X, double Y)[] BedCornersInImage => Options.Corners(_width, _height);

    public override Task StartAsync(CancellationToken cancellationToken = default)
    {
        IsRunning = true;
        Publish(Render());

        if (Options.FramesPerSecond > 0)
        {
            _loop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _loop.Token;
            var interval = TimeSpan.FromSeconds(1.0 / Options.FramesPerSecond);

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(interval, token).ConfigureAwait(false);
                    Publish(Render());
                }
            }, CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public override Task StopAsync()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
        IsRunning = false;
        return Task.CompletedTask;
    }

    public CameraFrame Render()
    {
        var frame = CameraFrame.Create(_width, _height, 30);
        var corners = Options.Corners(_width, _height);

        // Fill the quadrilateral by inverse-mapping each pixel back to bed space.
        var homography = HomographySolver.SolveOrIdentity(
        [
            (0, 0), (1, 0), (1, 1), (0, 1),
        ], corners);

        homography.TryInvert(out var toBed);

        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                var (u, v) = toBed.Apply(x + 0.5, y + 0.5);
                if (u is < 0 or > 1 || v is < 0 or > 1) continue;

                // Chequerboard ground. Kept well below the workpiece in luminance so a
                // histogram-based threshold has an unambiguous gap to find.
                var cell = ((int)(u * 20) + (int)(v * 20)) % 2 == 0;
                byte value = cell ? (byte)150 : (byte)120;

                // A markedly brighter rectangular workpiece.
                if (u > 0.28 && u < 0.72 && v > 0.30 && v < 0.68) value = 245;

                // Four dark circular fiducials just inside the corners.
                foreach (var (fx, fy) in Options.FiducialPositions)
                {
                    var du = u - fx;
                    var dv = v - fy;
                    if (du * du + dv * dv < Options.FiducialRadius * Options.FiducialRadius) value = 20;
                }

                frame.Set(x, y, value, value, value);
            }
        }

        return Options.BarrelDistortion == 0
            ? frame
            : LensModel.ApplyBarrel(frame, Options.BarrelDistortion);
    }
}

public sealed record SyntheticSceneOptions
{
    /// <summary>How far the bed is skewed in the image, 0 = square on. 0.18 is a typical lid-mounted camera.</summary>
    public double Perspective { get; init; } = 0.18;

    /// <summary>Positive values bulge the image outward, as a wide-angle lens does.</summary>
    public double BarrelDistortion { get; init; } = 0.12;

    public double FiducialRadius { get; init; } = 0.035;

    public IReadOnlyList<(double U, double V)> FiducialPositions { get; init; } =
    [
        (0.10, 0.10), (0.90, 0.10), (0.90, 0.90), (0.10, 0.90),
    ];

    /// <summary>Zero renders a single frame; above zero runs a live feed.</summary>
    public double FramesPerSecond { get; init; }

    public static readonly SyntheticSceneOptions Default = new();

    /// <summary>Bed corners in image pixels: TL, TR, BR, BL.</summary>
    public (double X, double Y)[] Corners(int width, int height)
    {
        var inset = 0.08;
        var skew = Perspective;

        // The far edge is narrower than the near edge, which is what a camera
        // looking down at an angle actually sees.
        var left = width * inset;
        var right = width * (1 - inset);
        var top = height * inset;
        var bottom = height * (1 - inset);
        var narrowing = (right - left) * skew / 2;

        return
        [
            (left + narrowing, top),
            (right - narrowing, top),
            (right, bottom),
            (left, bottom),
        ];
    }
}
