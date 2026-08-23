using System.Diagnostics;
using OpenBurn.Cam;
using OpenBurn.Cam.Import;
using OpenBurn.Cam.Trace;
// System.Diagnostics has a TraceOptions of its own, and this file needs Stopwatch.
using TraceOptions = OpenBurn.Cam.Trace.TraceOptions;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Jobs;
using OpenBurn.Core.Machines;
using OpenBurn.Core.Storage;
using OpenBurn.Devices;
using OpenBurn.GCode;
using OpenBurn.Materials;

namespace OpenBurn.App;

/// <summary>
/// A headless end-to-end check, run with <c>OpenBurn --selftest</c>.
///
/// The unit tests prove the libraries; this proves the *packaged application* —
/// that device profiles load from beside the executable, that the factory wires a
/// transport to a driver, and that a document becomes G-code and streams to
/// completion. Those are the things that break when a build is assembled wrongly
/// rather than when code is written wrongly, and they are invisible to a test run
/// against the source tree.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        var stopwatch = Stopwatch.StartNew();

        Console.WriteLine("OpenBurn self-test");
        Console.WriteLine("==================");
        Console.WriteLine();

        void Check(string what, bool ok, string? detail = null)
        {
            Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}{(detail is null ? "" : $" — {detail}")}");
            if (!ok) failures++;
        }

        // --- Bundled fonts ------------------------------------------------------
        var fonts = OpenBurn.Cam.Text.TextOutliner.RegisterBundledFonts();
        Check("bundled fonts register", fonts.Count > 0, string.Join(", ", fonts));

        // --- Machine profiles load from beside the executable -----------------
        var machines = MachineLibrary.Load();
        Check("machine profiles load", machines.Profiles.Count > 0, $"{machines.Profiles.Count} found");
        foreach (var error in machines.Errors) Check($"profile error: {error}", false);

        var virtualProfile = machines.Profiles.FirstOrDefault(p => p.Connections.Contains(ConnectionKind.Virtual))
                             ?? MachineProfile.Virtual();
        Check("a virtual machine is available", true, virtualProfile.DisplayName);

        // --- Materials ---------------------------------------------------------
        var materials = MaterialLibrary.CreateDefault();
        Check("material library loads", materials.Profiles.Count > 10, $"{materials.Profiles.Count} profiles");

        var plywood = materials.Find("Plywood", 3, 10);
        Check("material lookup by wattage", plywood is not null, plywood?.DisplayName);

        // --- SVG import --------------------------------------------------------
        var svgPath = FindSample("openburn-badge.svg");
        if (svgPath is not null)
        {
            var svg = SvgImporter.Import(File.ReadAllText(svgPath));
            Check("SVG import", svg.Paths.Count > 5, $"{svg.Paths.Count} paths, {svg.WidthMm:0}×{svg.HeightMm:0} mm");
        }
        else
        {
            Console.WriteLine("  [skip] SVG import — no sample file beside the executable");
        }

        // --- DXF import --------------------------------------------------------
        const string miniDxf = "0\nSECTION\n2\nHEADER\n9\n$INSUNITS\n70\n4\n0\nENDSEC\n" +
                               "0\nSECTION\n2\nENTITIES\n" +
                               "0\nLINE\n10\n0\n20\n0\n11\n100\n21\n0\n" +
                               "0\nCIRCLE\n10\n50\n20\n50\n40\n25\n" +
                               "0\nENDSEC\n0\nEOF\n";

        var dxf = DxfImporter.Parse(miniDxf);
        Check("DXF import", dxf.Paths.Count == 2, $"{dxf.Paths.Count} entities, {dxf.WidthMm:0} mm wide");

        // --- Text ---------------------------------------------------------------
        var text = OpenBurn.Cam.Text.TextOutliner.Create("Ob", new OpenBurn.Cam.Text.TextLayoutOptions
        {
            FontSizeMm = 20,
            FontFamily = fonts.FirstOrDefault() ?? "Sans Serif",
        });
        Check("text to outlines", text.Outlines.Count > 0 && !text.FontWasSubstituted,
            $"{text.Outlines.Count} contours in {text.ResolvedFamily}");

        // --- bitmap tracing ----------------------------------------------------
        var ring = new RasterImage(90, 90, BuildRing(90));

        var outlined = BitmapTracer.Trace(ring);
        Check("bitmap trace, outlines", outlined.ContourCount == 2 && outlined.Notes.Count == 0,
            $"{outlined.ContourCount} contours, {outlined.PointCount} points");

        var centred = BitmapTracer.Trace(ring, TraceOptions.Default with { Mode = TraceMode.Centreline });
        Check("bitmap trace, centrelines", centred.ContourCount == 1 && centred.Contours[0].IsClosed,
            $"{centred.ContourCount} stroke, closed={(centred.ContourCount > 0 && centred.Contours[0].IsClosed)}");

        var auto = BitmapTracer.AutoThreshold(ring);
        Check("trace auto threshold", auto is > 32 and < 224, auto.ToString());

        Check("trace preview renders", TracePreview.Render(ring, outlined.Contours, 240).Length > 100,
            $"{TracePreview.Render(ring, outlined.Contours, 240).Length} bytes of PNG");

        // --- CAM ---------------------------------------------------------------
        var design = Design.CreateDefault();
        design.Name = "self-test";
        design.Layers[0].Passes = 1;
        design.Layers[1].Passes = 1;
        design.AddShape(PathShape.Rectangle(60, 40), design.Layers[0]);
        design.Shapes[0].MoveTo(new Core.Geometry.Vec2(40, 40));

        var cam = CamPipeline.Generate(design, virtualProfile);
        Check("CAM generates a job", cam.Job.LineCount > 5, $"{cam.Job.LineCount} lines");
        Check("job passes validation", cam.CanRun,
            cam.CanRun ? null : string.Join("; ", cam.Issues.Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.Title)));
        Check("time estimate is sane", cam.Estimate.Total > TimeSpan.Zero, TimeEstimator.Format(cam.Estimate.Total));

        // --- Streaming to the simulator ----------------------------------------
        var transport = DeviceFactory.CreateTransport(virtualProfile, ConnectionKind.Virtual);
        await using var device = DeviceFactory.CreateDevice(virtualProfile);

        var faults = new List<string>();
        device.Fault += (info, isAlarm) => faults.Add($"{(isAlarm ? "ALARM" : "error")}:{info.Code}");

        await device.ConnectAsync(transport);
        Check("connects to the virtual machine", device.Connection == ConnectionState.Connected);

        var settingsDeadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (device.Settings.Count < 10 && DateTimeOffset.UtcNow < settingsDeadline) await Task.Delay(50);
        Check("reads controller settings", device.Settings.Count > 20, $"{device.Settings.Count} settings");

        await device.StartJobAsync(cam.Job);

        var jobDeadline = DateTimeOffset.UtcNow.AddSeconds(120);
        while (!device.JobState.IsTerminal() && DateTimeOffset.UtcNow < jobDeadline) await Task.Delay(50);

        Check("job runs to completion", device.JobState == JobState.Completed, device.JobState.ToString());
        Check("every line acknowledged",
            device.Progress.LinesAcknowledged == cam.Job.LineCount,
            $"{device.Progress.LinesAcknowledged}/{cam.Job.LineCount}");
        Check("no faults raised", faults.Count == 0, faults.Count == 0 ? null : string.Join(", ", faults));

        await device.DisconnectAsync();

        // --- Plugins ------------------------------------------------------------
        var pluginRegistry = new OpenBurn.Plugins.PluginRegistry();
        var pluginReport = OpenBurn.Plugins.PluginHost.Load(pluginRegistry, enabled: true);
        Check("plugin host runs", pluginReport.Failures.Count == 0, pluginReport.Summary);

        // --- Job library --------------------------------------------------------
        try
        {
            using var library = JobLibrary.InMemory();
            library.Record(new JobRecord
            {
                Name = "self-test",
                StartedAt = DateTimeOffset.UtcNow,
                Outcome = JobState.Completed,
            });
            Check("job library writes and reads", library.Count() == 1);
        }
        catch (Exception ex)
        {
            Check("job library writes and reads", false, ex.Message);
        }

        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"All checks passed in {stopwatch.ElapsedMilliseconds} ms."
            : $"{failures} check(s) FAILED in {stopwatch.ElapsedMilliseconds} ms.");

        return failures == 0 ? 0 : 1;
    }

    private static string? FindSample(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", name);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }

    /// <summary>A dark annulus on white: two contours as outlines, one as a centreline.</summary>
    private static byte[] BuildRing(int size)
    {
        var px = new byte[size * size];
        Array.Fill(px, (byte)255);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var d = Math.Sqrt((x - size / 2.0) * (x - size / 2.0) + (y - size / 2.0) * (y - size / 2.0));
                if (d > size * 0.28 && d < size * 0.36) px[y * size + x] = 0;
            }
        }
        return px;
    }
}
