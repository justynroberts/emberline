using OpenBurn.Camera;

namespace OpenBurn.Vision;

public sealed record Blob(
    int PixelCount,
    double CentreX,
    double CentreY,
    int MinX,
    int MinY,
    int MaxX,
    int MaxY)
{
    public int Width => MaxX - MinX + 1;
    public int Height => MaxY - MinY + 1;

    /// <summary>1.0 is a perfect square bounding box; a circle sits near 1.0 too.</summary>
    public double AspectRatio => Height == 0 ? 0 : (double)Width / Height;

    /// <summary>Filled fraction of the bounding box. A disc is about 0.785; a square is 1.0.</summary>
    public double Fill => Width * Height == 0 ? 0 : (double)PixelCount / (Width * Height);

    public bool LooksCircular => Math.Abs(AspectRatio - 1) < 0.35 && Fill is > 0.55 and < 0.95;
}

/// <summary>
/// Connected-component labelling over a thresholded image.
///
/// The workhorse behind both fiducial detection and workpiece detection. Iterative
/// flood fill rather than recursive, because a large bright region in a
/// high-resolution capture will otherwise overflow the stack — and it will always
/// be the one image the user cares about.
/// </summary>
public static class BlobDetector
{
    public static List<Blob> Find(byte[] greyscale, int width, int height, int threshold, bool findDark, int minimumPixels = 20)
    {
        var visited = new bool[greyscale.Length];
        var blobs = new List<Blob>();
        var stack = new Stack<int>();

        bool Matches(int index) => findDark ? greyscale[index] < threshold : greyscale[index] > threshold;

        for (var start = 0; start < greyscale.Length; start++)
        {
            if (visited[start] || !Matches(start)) continue;

            stack.Clear();
            stack.Push(start);
            visited[start] = true;

            var count = 0;
            long sumX = 0;
            long sumY = 0;
            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;

            while (stack.Count > 0)
            {
                var index = stack.Pop();
                var x = index % width;
                var y = index / width;

                count++;
                sumX += x;
                sumY += y;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;

                if (x > 0) Push(index - 1);
                if (x < width - 1) Push(index + 1);
                if (y > 0) Push(index - width);
                if (y < height - 1) Push(index + width);
            }

            if (count >= minimumPixels)
            {
                blobs.Add(new Blob(count, (double)sumX / count, (double)sumY / count, minX, minY, maxX, maxY));
            }

            void Push(int neighbour)
            {
                if (visited[neighbour] || !Matches(neighbour)) return;
                visited[neighbour] = true;
                stack.Push(neighbour);
            }
        }

        return blobs;
    }

    public static List<Blob> Find(CameraFrame frame, int threshold, bool findDark, int minimumPixels = 20) =>
        Find(frame.ToGreyscale(), frame.Width, frame.Height, threshold, findDark, minimumPixels);

    /// <summary>
    /// Otsu's method — the threshold that best separates the histogram into two
    /// classes.
    ///
    /// Returns the first value belonging to the *upper* class, so it can be used
    /// directly with the strict comparisons in <see cref="Find(byte[], int, int, int, bool, int)"/>:
    /// `value &lt; threshold` is the dark class and `value &gt; threshold - 1` is the
    /// light one. The textbook form returns the last value of the lower class, which
    /// is an off-by-one away from being usable and quietly loses the darkest pixels.
    /// </summary>
    public static int OtsuThreshold(byte[] greyscale)
    {
        var histogram = new int[256];
        foreach (var value in greyscale) histogram[value]++;

        var total = greyscale.Length;
        double sum = 0;
        for (var i = 0; i < 256; i++) sum += i * (double)histogram[i];

        double sumBackground = 0;
        var weightBackground = 0;
        var best = 0;
        var bestVariance = 0.0;

        for (var t = 0; t < 256; t++)
        {
            weightBackground += histogram[t];
            if (weightBackground == 0) continue;

            var weightForeground = total - weightBackground;
            if (weightForeground == 0) break;

            sumBackground += t * (double)histogram[t];
            var meanBackground = sumBackground / weightBackground;
            var meanForeground = (sum - sumBackground) / weightForeground;

            var between = (double)weightBackground * weightForeground *
                          (meanBackground - meanForeground) * (meanBackground - meanForeground);

            if (between > bestVariance)
            {
                bestVariance = between;
                best = t;
            }
        }

        return Math.Min(255, best + 1);
    }
}

/// <summary>
/// Finds the four dark registration marks on a calibration target or a jig.
///
/// Fiducial alignment is what lets someone put a pre-printed sheet on the bed at
/// any angle and have the artwork land on it: detect the marks, solve the
/// transform between where they are and where they should be, and apply it.
/// </summary>
public static class FiducialDetector
{
    public sealed record Result(IReadOnlyList<Point2> Markers, bool Found)
    {
        public static readonly Result NotFound = new([], false);
    }

    /// <summary>
    /// Detect four dark circular markers and return them in view order:
    /// top-left, top-right, bottom-right, bottom-left.
    /// </summary>
    public static Result FindFour(CameraFrame frame, int? threshold = null, int minimumPixels = 30)
    {
        var grey = frame.ToGreyscale();
        var cut = threshold ?? Math.Max(20, BlobDetector.OtsuThreshold(grey) / 2);

        var candidates = BlobDetector.Find(grey, frame.Width, frame.Height, cut, findDark: true, minimumPixels)
            .Where(b => b.LooksCircular)
            .OrderByDescending(b => b.PixelCount)
            .Take(12)
            .ToList();

        if (candidates.Count < 4) return Result.NotFound;

        // Of the plausible circles, take the four that sit furthest out — a
        // registration mark is at a corner, by definition.
        var centreX = candidates.Average(b => b.CentreX);
        var centreY = candidates.Average(b => b.CentreY);

        var corners = candidates
            .OrderByDescending(b => (b.CentreX - centreX) * (b.CentreX - centreX) +
                                    (b.CentreY - centreY) * (b.CentreY - centreY))
            .Take(4)
            .Select(b => new Point2(b.CentreX, b.CentreY))
            .ToList();

        return new Result(SortToViewOrder(corners), true);
    }

    /// <summary>Order four points as top-left, top-right, bottom-right, bottom-left.</summary>
    public static IReadOnlyList<Point2> SortToViewOrder(IReadOnlyList<Point2> points)
    {
        if (points.Count != 4) return points;

        var top = points.OrderBy(p => p.Y).Take(2).OrderBy(p => p.X).ToList();
        var bottom = points.OrderByDescending(p => p.Y).Take(2).OrderByDescending(p => p.X).ToList();

        return [top[0], top[1], bottom[0], bottom[1]];
    }
}

/// <summary>
/// Finds distinct workpieces on the bed — the four coasters problem from the PRD.
///
/// Runs on a rectified image, so every measurement it reports is already in
/// millimetres and can be used directly to duplicate and place artwork.
/// </summary>
public static class WorkpieceDetector
{
    public sealed record Workpiece(
        double CentreXMm,
        double CentreYMm,
        double WidthMm,
        double HeightMm,
        bool LooksCircular)
    {
        public string Describe() => LooksCircular
            ? $"round, {Math.Max(WidthMm, HeightMm):0.#} mm across, centred at {CentreXMm:0.#}, {CentreYMm:0.#}"
            : $"{WidthMm:0.#} × {HeightMm:0.#} mm, centred at {CentreXMm:0.#}, {CentreYMm:0.#}";
    }

    /// <summary>
    /// Detect objects in a rectified bed image.
    /// </summary>
    /// <param name="rectified">A top-down bed image from <see cref="BedRectifier.Rectify"/>.</param>
    /// <param name="pixelsPerMm">The scale that image was produced at.</param>
    /// <param name="minimumSizeMm">Ignore anything smaller — dust, honeycomb, shadows.</param>
    public static List<Workpiece> Detect(CameraFrame rectified, double pixelsPerMm, double minimumSizeMm = 8)
    {
        var grey = rectified.ToGreyscale();
        var threshold = BlobDetector.OtsuThreshold(grey);

        var minimumPixels = (int)Math.Max(16, minimumSizeMm * pixelsPerMm * minimumSizeMm * pixelsPerMm * 0.4);

        // Workpieces are normally lighter than the bed. Try light first, and fall
        // back to dark for a white bed or a black workpiece.
        var blobs = BlobDetector.Find(grey, rectified.Width, rectified.Height, threshold, findDark: false, minimumPixels);
        if (blobs.Count == 0)
        {
            blobs = BlobDetector.Find(grey, rectified.Width, rectified.Height, threshold, findDark: true, minimumPixels);
        }

        var bedArea = rectified.Width * (long)rectified.Height;

        return
        [
            .. blobs
                // Discard anything that is essentially the whole frame — that is the bed, not a part.
                .Where(b => b.PixelCount < bedArea * 0.75)
                .Select(b => new Workpiece(
                    b.CentreX / pixelsPerMm,
                    (rectified.Height - b.CentreY) / pixelsPerMm,
                    b.Width / pixelsPerMm,
                    b.Height / pixelsPerMm,
                    b.LooksCircular))
                .Where(w => w.WidthMm >= minimumSizeMm && w.HeightMm >= minimumSizeMm)
                .OrderByDescending(w => w.WidthMm * w.HeightMm),
        ];
    }
}
