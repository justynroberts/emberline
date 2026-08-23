using OpenBurn.Camera;
using OpenBurn.Vision;
using Xunit;

namespace OpenBurn.Vision.Tests;

public class HomographyTests
{
    [Fact]
    public void SolvesAnIdentityMapping()
    {
        (double, double)[] square = [(0, 0), (100, 0), (100, 100), (0, 100)];
        Assert.True(HomographySolver.TrySolve(square, square, out var h));

        var (x, y) = h.Apply(37, 62);
        Assert.Equal(37, x, 6);
        Assert.Equal(62, y, 6);
    }

    [Fact]
    public void SolvesAPureScale()
    {
        (double, double)[] from = [(0, 0), (1, 0), (1, 1), (0, 1)];
        (double, double)[] to = [(0, 0), (400, 0), (400, 300), (0, 300)];

        Assert.True(HomographySolver.TrySolve(from, to, out var h));

        var (x, y) = h.Apply(0.5, 0.5);
        Assert.Equal(200, x, 6);
        Assert.Equal(150, y, 6);
    }

    [Fact]
    public void SolvesAGenuinePerspectiveWarp()
    {
        // A trapezium — the far edge shorter than the near one, as a camera
        // looking down at an angle actually sees a bed.
        (double, double)[] bed = [(0, 0), (400, 0), (400, 400), (0, 400)];
        (double, double)[] image = [(120, 60), (520, 60), (610, 420), (30, 420)];

        Assert.True(HomographySolver.TrySolve(bed, image, out var h));

        // Every correspondence must be reproduced exactly.
        for (var i = 0; i < 4; i++)
        {
            var (x, y) = h.Apply(bed[i].Item1, bed[i].Item2);
            Assert.Equal(image[i].Item1, x, 4);
            Assert.Equal(image[i].Item2, y, 4);
        }
    }

    [Fact]
    public void InverseRoundTripsAnyPoint()
    {
        (double, double)[] bed = [(0, 0), (400, 0), (400, 400), (0, 400)];
        (double, double)[] image = [(120, 60), (520, 60), (610, 420), (30, 420)];

        Assert.True(HomographySolver.TrySolve(bed, image, out var forward));
        Assert.True(forward.TryInvert(out var back));

        foreach (var (px, py) in new[] { (137.0, 250.0), (300.0, 99.0), (12.5, 388.25) })
        {
            var (ix, iy) = forward.Apply(px, py);
            var (rx, ry) = back.Apply(ix, iy);
            Assert.Equal(px, rx, 4);
            Assert.Equal(py, ry, 4);
        }
    }

    [Fact]
    public void DegeneratePointsAreRejectedRatherThanReturningNonsense()
    {
        // All four points collinear: there is no valid homography.
        (double, double)[] line = [(0, 0), (1, 1), (2, 2), (3, 3)];
        (double, double)[] target = [(0, 0), (10, 0), (20, 0), (30, 0)];

        Assert.False(HomographySolver.TrySolve(line, target, out _));
    }
}

public class LensModelTests
{
    private static CameraFrame Chequerboard(int size, int cell)
    {
        var frame = CameraFrame.Create(size, size);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var on = (x / cell + y / cell) % 2 == 0;
                var v = on ? (byte)230 : (byte)40;
                frame.Set(x, y, v, v, v);
            }
        }
        return frame;
    }

    [Fact]
    public void ZeroDistortionIsALosslessCopy()
    {
        var source = Chequerboard(64, 8);
        var result = LensModel.Undistort(source, LensParameters.None);
        Assert.Equal(source.Pixels, result.Pixels);
    }

    [Fact]
    public void BarrelDistortionThenUndistortionRecoversTheImage()
    {
        // Two resamplings blur it, so compare the mean absolute error rather than
        // demanding identical bytes. The point is that the geometry comes back.
        var source = Chequerboard(128, 16);
        var distorted = LensModel.ApplyBarrel(source, 0.15);
        var recovered = LensModel.Undistort(distorted, new LensParameters(0.15));

        long error = 0;
        var counted = 0;
        // Ignore the outer border, where undistortion samples outside the source.
        for (var y = 24; y < 104; y++)
        {
            for (var x = 24; x < 104; x++)
            {
                var i = (y * 128 + x) * 4;
                error += Math.Abs(source.Pixels[i] - recovered.Pixels[i]);
                counted++;
            }
        }

        var mean = (double)error / counted;
        Assert.True(mean < 30, $"mean absolute error {mean:0.0} — the round trip did not recover the image");
    }

    [Fact]
    public void DistortionMovesPixelsAtAll()
    {
        var source = Chequerboard(128, 16);
        var distorted = LensModel.ApplyBarrel(source, 0.25);
        Assert.NotEqual(source.Pixels, distorted.Pixels);
    }

    [Fact]
    public void EstimatesK1FromABowedLine()
    {
        // Points that should be a straight horizontal line, bowed by a known amount.
        const int size = 400;
        var cx = size / 2.0;
        var cy = size / 2.0;
        var norm = Math.Sqrt(cx * cx + cy * cy);
        const double trueK = -0.20;

        var points = new List<(double X, double Y)>();
        for (var x = 40; x <= 360; x += 20)
        {
            var dx = (x - cx) / norm;
            var dy = (100 - cy) / norm;
            var r2 = dx * dx + dy * dy;
            var scale = 1 + trueK * r2;
            points.Add((cx + dx * scale * norm, cy + dy * scale * norm));
        }

        var estimated = LensModel.EstimateK1(points, size, size);

        // Recovering the sign and rough magnitude is what matters; the operator
        // then nudges it while watching the preview.
        Assert.True(estimated > 0.1, $"expected a positive correction, got {estimated:0.###}");
        Assert.InRange(estimated, 0.12, 0.36);
    }
}

public class BedRectifierTests
{
    private static CameraCalibration Calibrate(SyntheticCameraSource camera, int width, int height)
    {
        var corners = camera.BedCornersInImage.Select(c => new Point2(c.X, c.Y)).ToList();
        return BedRectifier.Calibrate("test-machine", "synthetic", corners, 400, 400);
    }

    [Fact]
    public void CalibrationFromExactCornersHasAlmostNoResidual()
    {
        var camera = new SyntheticCameraSource(640, 480, SyntheticSceneOptions.Default with { BarrelDistortion = 0 });
        var calibration = Calibrate(camera, 640, 480);

        Assert.True(calibration.IsUsable);
        Assert.True(calibration.ResidualPixels < 0.01, $"residual {calibration.ResidualPixels:0.####} px");
        Assert.Equal("Excellent", calibration.Quality);
    }

    [Fact]
    public void BedCornersMapToTheCornersOfTheRectifiedImage()
    {
        var camera = new SyntheticCameraSource(640, 480, SyntheticSceneOptions.Default with { BarrelDistortion = 0 });
        var calibration = Calibrate(camera, 640, 480);
        var rectifier = new BedRectifier(calibration);

        var image = camera.BedCornersInImage;

        // Front-left of the bed is the origin, and it is the fourth corner in view order.
        var (x, y) = rectifier.ImageToBed(image[3].X, image[3].Y);
        Assert.Equal(0, x, 3);
        Assert.Equal(0, y, 3);

        // Rear-right of the bed is the second corner in view order.
        var (x2, y2) = rectifier.ImageToBed(image[1].X, image[1].Y);
        Assert.Equal(400, x2, 3);
        Assert.Equal(400, y2, 3);
    }

    [Fact]
    public void BedAndImageCoordinatesRoundTrip()
    {
        var camera = new SyntheticCameraSource(640, 480, SyntheticSceneOptions.Default with { BarrelDistortion = 0 });
        var rectifier = new BedRectifier(Calibrate(camera, 640, 480));

        foreach (var (bx, by) in new[] { (10.0, 10.0), (200.0, 200.0), (390.0, 20.0), (123.4, 321.0) })
        {
            var (ix, iy) = rectifier.BedToImage(bx, by);
            var (rx, ry) = rectifier.ImageToBed(ix, iy);
            Assert.Equal(bx, rx, 3);
            Assert.Equal(by, ry, 3);
        }
    }

    [Fact]
    public void RectifyingProducesATopDownImageAtTheRequestedScale()
    {
        var camera = new SyntheticCameraSource(800, 600, SyntheticSceneOptions.Default with { BarrelDistortion = 0 });
        var rectifier = new BedRectifier(Calibrate(camera, 800, 600));

        var rectified = rectifier.Rectify(camera.Render(), pixelsPerMm: 1.5);

        Assert.Equal(600, rectified.Width);   // 400 mm × 1.5
        Assert.Equal(600, rectified.Height);

        // The centre of the bed carries the bright workpiece from the synthetic scene.
        var (r, _, _, _) = rectified[300, 300];
        Assert.True(r > 200, $"expected the workpiece at the bed centre, got luminance {r}");
    }

    [Fact]
    public void TheWorkpieceIsSquareAfterRectificationEvenThoughItIsSkewedInTheImage()
    {
        // This is the whole point of the perspective correction: an object that is
        // a trapezium in the raw camera view must come out rectangular.
        var camera = new SyntheticCameraSource(800, 600, SyntheticSceneOptions.Default with
        {
            Perspective = 0.25,
            BarrelDistortion = 0,
        });

        var rectifier = new BedRectifier(Calibrate(camera, 800, 600));
        var rectified = rectifier.Rectify(camera.Render(), pixelsPerMm: 2.0);

        var found = WorkpieceDetector.Detect(rectified, 2.0, minimumSizeMm: 20);
        Assert.NotEmpty(found);

        var workpiece = found[0];

        // The synthetic workpiece spans u 0.28–0.72 and v 0.30–0.68 of a 400 mm bed:
        // 176 mm wide by 152 mm tall. Edge softening from two resampling passes
        // costs a few millimetres either way, so the sizes are checked loosely and
        // the aspect ratio — the thing perspective correction actually fixes — tightly.
        Assert.InRange(workpiece.WidthMm, 160, 200);
        Assert.InRange(workpiece.HeightMm, 135, 175);
        Assert.Equal(176.0 / 152.0, workpiece.WidthMm / workpiece.HeightMm, 1);
    }
}

public class DetectorTests
{
    [Fact]
    public void OtsuSeparatesABimodalHistogram()
    {
        var grey = new byte[1000];
        for (var i = 0; i < 500; i++) grey[i] = 30;
        for (var i = 500; i < 1000; i++) grey[i] = 220;

        var threshold = BlobDetector.OtsuThreshold(grey);

        // The returned value is the first of the upper class, so both populations
        // must be classified correctly by the strict comparisons Find() uses.
        Assert.InRange(threshold, 31, 220);
        Assert.True(30 < threshold, "dark pixels must fall below the threshold");
        Assert.True(220 >= threshold, "light pixels must fall at or above the threshold");
    }

    [Fact]
    public void FindsSeparateBlobsAndTheirCentres()
    {
        const int w = 100, h = 100;
        var grey = new byte[w * h];
        Array.Fill(grey, (byte)255);

        void Square(int x0, int y0, int size)
        {
            for (var y = y0; y < y0 + size; y++)
            {
                for (var x = x0; x < x0 + size; x++) grey[y * w + x] = 0;
            }
        }

        Square(10, 10, 20);
        Square(60, 60, 20);

        var blobs = BlobDetector.Find(grey, w, h, 128, findDark: true);

        Assert.Equal(2, blobs.Count);
        Assert.All(blobs, b => Assert.Equal(400, b.PixelCount));

        var first = blobs.OrderBy(b => b.CentreX).First();
        Assert.Equal(19.5, first.CentreX, 1);
        Assert.Equal(19.5, first.CentreY, 1);
    }

    [Fact]
    public void LargeRegionsDoNotOverflowTheStack()
    {
        // A recursive flood fill dies here; the iterative one must not.
        const int w = 900, h = 900;
        var grey = new byte[w * h];
        Array.Fill(grey, (byte)0);

        var blobs = BlobDetector.Find(grey, w, h, 128, findDark: true);
        Assert.Single(blobs);
        Assert.Equal(w * h, blobs[0].PixelCount);
    }

    [Fact]
    public void FindsTheFourFiducialsInASyntheticScene()
    {
        var camera = new SyntheticCameraSource(800, 600, SyntheticSceneOptions.Default with { BarrelDistortion = 0 });
        var result = FiducialDetector.FindFour(camera.Render());

        Assert.True(result.Found);
        Assert.Equal(4, result.Markers.Count);

        // View order: top-left first, and the two top markers above the two bottom ones.
        var markers = result.Markers;
        Assert.True(markers[0].X < markers[1].X, "first marker should be left of the second");
        Assert.True(markers[0].Y < markers[3].Y, "top markers should be above bottom markers");
    }

    [Fact]
    public void FiducialsResolveToTheirTrueBedPositions()
    {
        var camera = new SyntheticCameraSource(800, 600, SyntheticSceneOptions.Default with { BarrelDistortion = 0 });
        var corners = camera.BedCornersInImage.Select(c => new Point2(c.X, c.Y)).ToList();
        var rectifier = new BedRectifier(BedRectifier.Calibrate("m", "c", corners, 400, 400));

        var found = FiducialDetector.FindFour(camera.Render());
        Assert.True(found.Found);

        // The scene places markers at 10% and 90% of the bed in u and v, and v runs
        // from the rear, so bed Y is 400 − v·400.
        var topLeft = rectifier.ImageToBed(found.Markers[0].X, found.Markers[0].Y);
        Assert.InRange(topLeft.X, 30, 50);    // 0.10 × 400 = 40 mm
        Assert.InRange(topLeft.Y, 350, 370);  // 400 − 40 = 360 mm
    }

    [Fact]
    public void DetectsFourSeparateCoasters()
    {
        // The scenario straight from the PRD: four round workpieces on the bed.
        const double pixelsPerMm = 2.0;
        var frame = CameraFrame.Create(800, 800, 40);

        void Disc(double cxMm, double cyMm, double radiusMm)
        {
            var cx = cxMm * pixelsPerMm;
            var cy = cyMm * pixelsPerMm;
            var r = radiusMm * pixelsPerMm;

            for (var y = (int)(cy - r); y <= cy + r; y++)
            {
                for (var x = (int)(cx - r); x <= cx + r; x++)
                {
                    if (x < 0 || y < 0 || x >= 800 || y >= 800) continue;
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > r * r) continue;
                    frame.Set(x, y, 235, 235, 235);
                }
            }
        }

        Disc(100, 100, 50);
        Disc(300, 100, 50);
        Disc(100, 300, 50);
        Disc(300, 300, 50);

        var found = WorkpieceDetector.Detect(frame, pixelsPerMm, minimumSizeMm: 20);

        Assert.Equal(4, found.Count);
        Assert.All(found, w =>
        {
            Assert.True(w.LooksCircular, $"expected a round workpiece, got {w.Describe()}");
            Assert.InRange(w.WidthMm, 90, 110);
        });
    }
}
