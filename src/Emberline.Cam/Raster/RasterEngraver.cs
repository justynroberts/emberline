using Emberline.Core.Documents;
using Emberline.Core.Units;
using Emberline.GCode;

namespace Emberline.Cam.Raster;

public enum ScanPattern
{
    Horizontal,
    Vertical,
    /// <summary>Free angle, set by <see cref="RasterOptions.ScanAngleDegrees"/>.</summary>
    Angled,
}

public sealed record RasterOptions
{
    /// <summary>Engraved size on the bed, millimetres.</summary>
    public double WidthMm { get; init; } = 100;
    public double HeightMm { get; init; } = 100;

    /// <summary>Bottom-left corner in work coordinates.</summary>
    public double OriginX { get; init; }
    public double OriginY { get; init; }

    /// <summary>Distance between scan lines. 0.1 mm is roughly 254 DPI.</summary>
    public double LineIntervalMm { get; init; } = 0.1;

    public ScanPattern Scan { get; init; } = ScanPattern.Horizontal;
    public double ScanAngleDegrees { get; init; } = 45;

    public bool Bidirectional { get; init; } = true;

    /// <summary>Run-up either side of each line, millimetres.</summary>
    public double OverscanMm { get; init; } = 2;

    public double FeedMmMin { get; init; } = 3000;
    public double TravelFeedMmMin { get; init; } = 6000;

    /// <summary>0–100.</summary>
    public double MinPowerPercent { get; init; }
    public double MaxPowerPercent { get; init; } = 100;

    /// <summary>The S value meaning 100 %. GRBL's $30.</summary>
    public int MaxSpindle { get; init; } = 1000;

    /// <summary>M4 dynamic power is strongly preferred; M3 is for controllers without it.</summary>
    public bool DynamicPower { get; init; } = true;

    public int Passes { get; init; } = 1;

    /// <summary>Skip white runs at least this long with a rapid, millimetres.</summary>
    public double SkipThresholdMm { get; init; } = 1;

    public bool EmitHeader { get; init; } = true;

    public static readonly RasterOptions Default = new();

    public double Dpi => UnitConvert.IntervalToDpi(LineIntervalMm);
}

public sealed record RasterResult(IReadOnlyList<string> Lines, int BurnMoves, int TravelMoves, int Rows)
{
    public string Text => string.Join('\n', Lines);
}

/// <summary>
/// Turns a prepared bitmap into scan-line G-code.
///
/// The four things that decide whether a raster engrave looks good and finishes
/// this century are all handled here:
///
///  * **Run merging.** Consecutive pixels at the same power become one G1. A
///    thousand-pixel photo row is typically sixty to two hundred moves, not a
///    thousand, and that is the difference between a smooth engrave and one that
///    stutters as the planner starves.
///  * **White skipping.** Blank stretches become rapids instead of zero-power cuts.
///  * **Overscan.** Acceleration happens outside the artwork, so the first and last
///    pixel of every row get the same dwell as the ones in the middle. Without it
///    every raster has darker edges.
///  * **M4 dynamic power**, so output tracks actual feed rate through acceleration.
/// </summary>
public static class RasterEngraver
{
    /// <summary>
    /// Pixel dimensions the image should be resampled to.
    ///
    /// The along-scan pitch matches the line interval, which makes the dots square.
    /// Anything else distorts the dither pattern into visible stripes.
    /// </summary>
    public static (int Width, int Height) TargetPixelSize(RasterOptions o)
    {
        var interval = Math.Max(0.01, o.LineIntervalMm);
        return (Math.Max(1, (int)Math.Round(o.WidthMm / interval)),
                Math.Max(1, (int)Math.Round(o.HeightMm / interval)));
    }

    internal readonly record struct Run(int Start, int End, int Spindle);

    /// <summary>Merge a scan row into constant-power runs.</summary>
    internal static List<Run> RowToRuns(ReadOnlySpan<byte> row, int minSpindle, int maxSpindle)
    {
        var runs = new List<Run>();
        var currentSpindle = -1;
        var start = 0;

        for (var i = 0; i < row.Length; i++)
        {
            var v = row[i];
            // 255 is white, which means leave the material alone. Everything darker
            // maps linearly into the power range.
            var spindle = v >= 255
                ? 0
                : (int)Math.Round(minSpindle + (255 - v) / 255.0 * (maxSpindle - minSpindle));

            if (spindle != currentSpindle)
            {
                if (currentSpindle >= 0 && i > start) runs.Add(new Run(start, i, currentSpindle));
                currentSpindle = spindle;
                start = i;
            }
        }

        if (currentSpindle >= 0 && row.Length > start) runs.Add(new Run(start, row.Length, currentSpindle));
        return runs;
    }

    public static RasterResult Generate(RasterImage image, RasterOptions? options = null)
    {
        var o = options ?? RasterOptions.Default;
        var writer = new GcodeWriter();

        var minSpindle = (int)Math.Round(Math.Clamp(o.MinPowerPercent, 0, 100) / 100.0 * o.MaxSpindle);
        var maxSpindle = (int)Math.Round(Math.Clamp(o.MaxPowerPercent, 0, 100) / 100.0 * o.MaxSpindle);

        if (o.EmitHeader)
        {
            writer.Comment("Generated by Emberline");
            writer.Comment($"Raster {image.Width}×{image.Height} px -> {o.WidthMm:0.##}×{o.HeightMm:0.##} mm");
            writer.Comment($"Interval {o.LineIntervalMm:0.###} mm ({o.Dpi:0} DPI), scan {o.Scan}" +
                           (o.Scan == ScanPattern.Angled ? $" {o.ScanAngleDegrees:0.#}°" : string.Empty));
            writer.Comment($"Power {o.MinPowerPercent:0.#}–{o.MaxPowerPercent:0.#}% of S{o.MaxSpindle}, " +
                           $"feed {o.FeedMmMin:0} mm/min, {(o.DynamicPower ? "M4" : "M3")}, {o.Passes} pass(es)");
        }

        writer.Raw("G21");
        writer.Raw("G90");
        writer.Raw("G17");
        writer.Raw(o.DynamicPower ? "M4 S0" : "M3 S0");

        var pixelWidthMm = o.WidthMm / image.Width;
        var pixelHeightMm = o.HeightMm / image.Height;

        RasterImage working;
        Func<double, double, (double X, double Y)> toMm;

        switch (o.Scan)
        {
            case ScanPattern.Vertical:
                working = ImageProcessor.Transpose(image);
                // After transposing, walking a row walks the original Y axis.
                toMm = (px, py) => (o.OriginX + py * pixelWidthMm, o.OriginY + (image.Height - px) * pixelHeightMm);
                break;

            case ScanPattern.Angled:
            {
                var (rotated, ncx, ncy) = ImageProcessor.RotateFree(image, o.ScanAngleDegrees);
                working = rotated;
                var rad = o.ScanAngleDegrees * Math.PI / 180;
                var (sin, cos) = Math.SinCos(rad);
                var cx = image.Width / 2.0;
                var cy = image.Height / 2.0;
                toMm = (px, py) =>
                {
                    var dx = px - ncx;
                    var dy = py - ncy;
                    var ox = cos * dx + sin * dy + cx;
                    var oy = -sin * dx + cos * dy + cy;
                    return (o.OriginX + ox * pixelWidthMm, o.OriginY + (image.Height - oy) * pixelHeightMm);
                };
                break;
            }

            default:
                working = image;
                // Image row 0 is the top of the picture; machine Y grows upward.
                toMm = (px, py) => (o.OriginX + px * pixelWidthMm, o.OriginY + (image.Height - py) * pixelHeightMm);
                break;
        }

        var stepMm = o.Scan == ScanPattern.Vertical ? pixelHeightMm : pixelWidthMm;
        var overscanPixels = o.OverscanMm > 0 ? o.OverscanMm / Math.Max(stepMm, 1e-6) : 0;
        var skipPixels = Math.Max(2, o.SkipThresholdMm / Math.Max(stepMm, 1e-6));

        var rows = 0;
        var leftToRight = true;

        for (var pass = 0; pass < Math.Max(1, o.Passes); pass++)
        {
            if (pass > 0)
            {
                writer.Comment($"pass {pass + 1}");
                writer.ResetModal();
            }

            for (var y = 0; y < working.Height; y++)
            {
                var row = working.Pixels.AsSpan(y * working.Width, working.Width);
                var runs = RowToRuns(row, minSpindle, maxSpindle);
                var burning = runs.Where(r => r.Spindle > 0).ToList();
                if (burning.Count == 0) continue;

                rows++;
                var ordered = leftToRight ? burning : Enumerable.Reverse(burning).ToList();
                var first = ordered[0];
                var last = ordered[^1];

                // Lead-in: arrive `overscan` before the first burn with the beam off.
                var leadPixel = leftToRight ? first.Start - overscanPixels : last.End + overscanPixels;
                var lead = toMm(leadPixel, y + 0.5);
                writer.Rapid(lead.X, lead.Y, o.TravelFeedMmMin);
                writer.SetSpindle(0);

                var cursor = leftToRight ? (double)first.Start : last.End;
                if (overscanPixels > 0)
                {
                    var p = toMm(cursor, y + 0.5);
                    writer.Linear(p.X, p.Y, 0, o.FeedMmMin);
                }

                foreach (var run in ordered)
                {
                    var runStart = leftToRight ? run.Start : run.End;
                    var runEnd = leftToRight ? run.End : run.Start;

                    var gap = Math.Abs(runStart - cursor);
                    if (gap > 0)
                    {
                        var p = toMm(runStart, y + 0.5);
                        if (gap >= skipPixels) writer.Rapid(p.X, p.Y, o.TravelFeedMmMin);
                        else writer.Linear(p.X, p.Y, 0, o.FeedMmMin);
                    }

                    var end = toMm(runEnd, y + 0.5);
                    writer.Linear(end.X, end.Y, run.Spindle, o.FeedMmMin);
                    cursor = runEnd;
                }

                if (overscanPixels > 0)
                {
                    // Lead-out, so deceleration also happens clear of the artwork.
                    var outPixel = leftToRight ? cursor + overscanPixels : cursor - overscanPixels;
                    var p = toMm(outPixel, y + 0.5);
                    writer.Linear(p.X, p.Y, 0, o.FeedMmMin);
                }
                else
                {
                    writer.SetSpindle(0);
                }

                if (o.Bidirectional) leftToRight = !leftToRight;
            }
        }

        writer.SetSpindle(0);
        writer.Raw("M5");
        writer.Rapid(o.OriginX, o.OriginY, o.TravelFeedMmMin);

        return new RasterResult(writer.Lines, writer.BurnMoveCount, writer.TravelMoveCount, rows);
    }

    /// <summary>
    /// The whole raster path in one call: adjust, resample to the engraving grid,
    /// dither, then generate. This is what the CAM pipeline and the AI test-grid
    /// generator both use, so there is exactly one code path to trust.
    /// </summary>
    public static RasterResult Process(
        RasterImage source,
        ImageAdjustments adjustments,
        Dither.Options ditherOptions,
        RasterOptions rasterOptions)
    {
        var adjusted = ImageProcessor.Apply(source, adjustments);
        var (targetWidth, targetHeight) = TargetPixelSize(rasterOptions);
        var resampled = ImageProcessor.Resample(adjusted, targetWidth, targetHeight);
        var dithered = Dither.Apply(resampled, ditherOptions);
        return Generate(dithered, rasterOptions);
    }
}
