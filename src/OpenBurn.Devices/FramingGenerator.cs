using System.Globalization;
using OpenBurn.Core.Geometry;
using OpenBurn.Core.Jobs;
using OpenBurn.Core.Machines;
using OpenBurn.GCode;

namespace OpenBurn.Devices;

/// <summary>
/// Builds the G-code for a framing pass.
///
/// Framing is the last chance to notice the artwork is 20 mm off before the beam
/// marks the workpiece, so it is worth getting the details right: the head must
/// return to where it started, the power must be low enough to be harmless but
/// high enough to see, and the trace must reflect what will actually be burned
/// rather than the bounding box of the rapids.
/// </summary>
public static class FramingGenerator
{
    public static IReadOnlyList<string> Build(
        IReadOnlyList<Polyline> outlines,
        FramingOptions options,
        MachineProfile machine)
    {
        var writer = new GcodeWriter();
        writer.Comment($"OpenBurn framing pass — {options.Mode}");

        var points = outlines.SelectMany(p => p.Points).ToList();
        if (points.Count == 0)
        {
            writer.Comment("nothing to frame");
            return writer.Lines;
        }

        var spindle = machine.PowerToSpindle(options.PowerPercent);

        writer.Raw("G21");
        writer.Raw("G90");
        // M3 constant power, not M4: during a frame the head is often moving slowly
        // and dynamic power would fade the pointer out at the corners, which is
        // precisely where the operator is looking.
        writer.Raw(spindle > 0 ? "M3 S0" : "M5");

        var path = BuildFramePath(outlines, options, points);
        if (path.Count == 0)
        {
            writer.Comment("frame path collapsed to a point");
            return writer.Lines;
        }

        var start = path[0];
        writer.Rapid(start.X, start.Y, machine.TravelSpeedMmMin);

        for (var repeat = 0; repeat < Math.Max(1, options.Repeats); repeat++)
        {
            for (var i = 1; i < path.Count; i++)
            {
                writer.Linear(path[i].X, path[i].Y, spindle, options.FeedRate);
            }
            writer.Linear(start.X, start.Y, spindle, options.FeedRate);
        }

        writer.SetSpindle(0);
        writer.Raw("M5");
        writer.Comment($"frame complete — {path.Count} points, {options.Repeats} pass(es)");
        return writer.Lines;
    }

    private static List<Vec2> BuildFramePath(IReadOnlyList<Polyline> outlines, FramingOptions options, List<Vec2> points)
    {
        switch (options.Mode)
        {
            case FramingMode.Hull:
            {
                var hull = PathOps.ConvexHull(points);
                var expanded = ExpandFromCentroid(hull.Points, options.MarginMm);
                return expanded;
            }

            case FramingMode.Outline:
            {
                // Trace each outline in travel-optimised order.
                var ordered = PathOps.OptimiseTravel(outlines, Vec2.Zero);
                var path = new List<Vec2>();
                foreach (var poly in ordered)
                {
                    path.AddRange(poly.Points);
                    if (poly.IsClosed && poly.Count > 0) path.Add(poly.First);
                }
                return path;
            }

            default:
            {
                var bounds = Rect2.FromPoints(points).Inflate(options.MarginMm);
                return
                [
                    new Vec2(bounds.MinX, bounds.MinY),
                    new Vec2(bounds.MaxX, bounds.MinY),
                    new Vec2(bounds.MaxX, bounds.MaxY),
                    new Vec2(bounds.MinX, bounds.MaxY),
                ];
            }
        }
    }

    /// <summary>Push hull vertices outward from the centroid to apply a margin.</summary>
    private static List<Vec2> ExpandFromCentroid(IReadOnlyList<Vec2> hull, double margin)
    {
        if (margin <= 0 || hull.Count == 0) return [.. hull];

        var cx = hull.Average(p => p.X);
        var cy = hull.Average(p => p.Y);
        var centre = new Vec2(cx, cy);

        var result = new List<Vec2>(hull.Count);
        foreach (var p in hull)
        {
            var dir = (p - centre).Normalized;
            result.Add(dir.LengthSquared < 1e-12 ? p : p + dir * margin);
        }
        return result;
    }

    /// <summary>A one-line description for the confirmation prompt.</summary>
    public static string Describe(IReadOnlyList<Polyline> outlines, FramingOptions options)
    {
        var bounds = Rect2.FromPoints(outlines.SelectMany(p => p.Points));
        if (bounds.IsEmpty) return "Nothing to frame.";

        return string.Create(CultureInfo.InvariantCulture,
            $"Trace {bounds.Width:0.#} × {bounds.Height:0.#} mm at {options.FeedRate:0} mm/min, {options.PowerPercent:0.##}% power.");
    }
}
