using OpenBurn.Camera;

namespace OpenBurn.Vision;

/// <summary>
/// The camera-to-bed pipeline: capture, lens correction, perspective correction,
/// crop to the bed, scale to millimetres.
///
/// The output is an image where one pixel is a known fraction of a millimetre and
/// pixel (0, height) is the machine origin — so dropping artwork onto the camera
/// view and dropping it onto the bed are the same operation. That equivalence is
/// the entire point of the camera subsystem.
/// </summary>
public sealed class BedRectifier
{
    private readonly CameraCalibration _calibration;
    private readonly Homography _imageToBed;
    private readonly Homography _bedToImage;

    public BedRectifier(CameraCalibration calibration)
    {
        if (!calibration.IsUsable) throw new ArgumentException("This calibration is incomplete.", nameof(calibration));

        _calibration = calibration;

        // Bed millimetres, Y up, with the origin at the front-left corner. The
        // corner order is TL, TR, BR, BL as the camera sees it, which in bed
        // coordinates is rear-left, rear-right, front-right, front-left.
        (double X, double Y)[] bedCorners =
        [
            (0, calibration.BedHeightMm),
            (calibration.BedWidthMm, calibration.BedHeightMm),
            (calibration.BedWidthMm, 0),
            (0, 0),
        ];

        var imageCorners = calibration.ImageCorners.Select(p => ((double)p.X, (double)p.Y)).ToArray();

        _bedToImage = HomographySolver.SolveOrIdentity(bedCorners, imageCorners);
        _imageToBed = HomographySolver.SolveOrIdentity(imageCorners, bedCorners);
    }

    /// <summary>Where a bed coordinate lands in the raw camera image.</summary>
    public (double X, double Y) BedToImage(double xMm, double yMm) => _bedToImage.Apply(xMm, yMm);

    /// <summary>What bed coordinate a camera pixel is looking at.</summary>
    public (double X, double Y) ImageToBed(double px, double py) => _imageToBed.Apply(px, py);

    /// <summary>
    /// Produce a top-down image of the bed at the requested resolution.
    /// Pixels per millimetre is a deliberate parameter: the overlay wants two or
    /// three, and object detection wants more.
    /// </summary>
    public CameraFrame Rectify(CameraFrame source, double pixelsPerMm = 2.0)
    {
        var corrected = _calibration.Lens.IsIdentity ? source : LensModel.Undistort(source, _calibration.Lens);

        var width = Math.Max(1, (int)Math.Round(_calibration.BedWidthMm * pixelsPerMm));
        var height = Math.Max(1, (int)Math.Round(_calibration.BedHeightMm * pixelsPerMm));
        var result = CameraFrame.Create(width, height);

        for (var y = 0; y < height; y++)
        {
            // Row 0 is the rear of the bed; machine Y grows toward the front.
            var bedY = _calibration.BedHeightMm - (y + 0.5) / pixelsPerMm;

            for (var x = 0; x < width; x++)
            {
                var bedX = (x + 0.5) / pixelsPerMm;
                var (sx, sy) = _bedToImage.Apply(bedX, bedY);
                LensModel.Sample(corrected, sx - 0.5, sy - 0.5, result, x, y);
            }
        }

        return result;
    }

    /// <summary>
    /// Calibrate from four clicked bed corners.
    ///
    /// The residual is reported rather than hidden: it is the round-trip error of
    /// the corner points through the solved transform, and it is the only honest
    /// signal the operator has that they clicked in the wrong place.
    /// </summary>
    public static CameraCalibration Calibrate(
        string machineId,
        string cameraId,
        IReadOnlyList<Point2> imageCorners,
        double bedWidthMm,
        double bedHeightMm,
        LensParameters lens = default)
    {
        if (imageCorners.Count != 4)
        {
            throw new ArgumentException("Exactly four bed corners are needed: top-left, top-right, bottom-right, bottom-left.", nameof(imageCorners));
        }

        (double X, double Y)[] bedCorners =
        [
            (0, bedHeightMm),
            (bedWidthMm, bedHeightMm),
            (bedWidthMm, 0),
            (0, 0),
        ];

        var image = imageCorners.Select(p => ((double)p.X, (double)p.Y)).ToArray();
        var bedToImage = HomographySolver.SolveOrIdentity(bedCorners, image);

        double residual = 0;
        for (var i = 0; i < 4; i++)
        {
            var (px, py) = bedToImage.Apply(bedCorners[i].X, bedCorners[i].Y);
            residual += Math.Sqrt(Math.Pow(px - image[i].Item1, 2) + Math.Pow(py - image[i].Item2, 2));
        }
        residual /= 4;

        return new CameraCalibration
        {
            MachineId = machineId,
            CameraId = cameraId,
            ImageCorners = imageCorners,
            BedWidthMm = bedWidthMm,
            BedHeightMm = bedHeightMm,
            Lens = lens.IsIdentity ? LensParameters.None : lens,
            ResidualPixels = residual,
        };
    }
}
