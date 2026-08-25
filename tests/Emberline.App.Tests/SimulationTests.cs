using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Emberline.App.ViewModels;
using Emberline.Core.Documents;
using Emberline.Core.Geometry;
using Emberline.Core.Storage;
using Xunit;

namespace Emberline.App.Tests;

/// <summary>
/// The shell's simulation and G-code inspection, driven through the real view
/// model with a real generated job.
///
/// These are the parts the PRD asks for by name — G-code always inspectable, and a
/// preview that shows the job before a workpiece is committed — so they are worth
/// testing against the actual pipeline rather than a stub toolpath.
/// </summary>
public class SimulationTests : ShellTest
{
    private MainViewModel CreateShell()
    {
        // Point application storage at a scratch directory so a test run never
        // touches the machine's real settings or job history.

        var shell = NewShell();

        var square = new PathShape([new Polyline(
        [
            new Vec2(50, 50), new Vec2(150, 50), new Vec2(150, 150), new Vec2(50, 150),
        ], closed: true)]);

        shell.Design.AddShape(square, shell.Design.Layers[0]);

        // Bypass the debounce rather than waiting on a timer that the headless
        // platform does not drive.
        shell.RegenerateNow();

        return shell;
    }

    [AvaloniaFact]
    public void GeneratingAJobProducesInspectableGcode()
    {
        var shell = CreateShell();

        Assert.DoesNotContain("No job generated", shell.GcodeText, StringComparison.Ordinal);
        Assert.Contains("G21", shell.GcodeText, StringComparison.Ordinal);
        Assert.Contains("M5", shell.GcodeText, StringComparison.Ordinal);

        // Line numbers, so a validation warning can be pointed at one.
        Assert.Contains("     1  ", shell.GcodeText, StringComparison.Ordinal);
        Assert.Contains("lines", shell.GcodeSummary, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheSimulationBarIsHiddenUntilPlaybackStarts()
    {
        var shell = CreateShell();
        Assert.False(shell.SimulationVisible);

        shell.PlaySimulationCommand.Execute(null);

        Assert.True(shell.SimulationVisible);
        Assert.True(shell.IsSimulating);
    }

    [AvaloniaFact]
    public void ScrubbingMovesTheHeadAlongTheToolpath()
    {
        var shell = CreateShell();
        shell.PlaySimulationCommand.Execute(null);
        shell.PauseSimulationCommand.Execute(null);

        shell.SimulationFraction = 0;
        var start = shell.DisplayHeadPosition;

        shell.SimulationFraction = 0.5;
        var middle = shell.DisplayHeadPosition;

        shell.SimulationFraction = 1;
        var end = shell.DisplayHeadPosition;

        Assert.NotNull(start);
        Assert.NotNull(middle);
        Assert.NotNull(end);
        Assert.NotEqual(start, middle);
        Assert.NotEqual(middle, end);
    }

    [AvaloniaFact]
    public void ProgressAdvancesMonotonicallyWhileScrubbing()
    {
        var shell = CreateShell();
        shell.PlaySimulationCommand.Execute(null);
        shell.PauseSimulationCommand.Execute(null);

        var previous = -1;
        for (var i = 0; i <= 20; i++)
        {
            shell.SimulationFraction = i / 20.0;
            var segment = shell.ProgressSegment;
            Assert.True(segment >= previous, $"progress went backwards at {i * 5}%");
            previous = segment;
        }
    }

    [AvaloniaFact]
    public void TheStatusLineDescribesWhatTheHeadIsDoing()
    {
        var shell = CreateShell();
        shell.PlaySimulationCommand.Execute(null);
        shell.PauseSimulationCommand.Execute(null);

        shell.SimulationFraction = 0.5;

        Assert.Contains("of", shell.SimulationStatus, StringComparison.Ordinal);
        Assert.Matches(@"(Rapid|Travel|Engrave|Cut)", shell.SimulationStatus);
    }

    [AvaloniaFact]
    public void StoppingRewindsAndDismissesTheBar()
    {
        var shell = CreateShell();
        shell.PlaySimulationCommand.Execute(null);
        shell.SimulationFraction = 0.7;

        shell.StopSimulationCommand.Execute(null);

        Assert.False(shell.IsSimulating);
        Assert.False(shell.SimulationVisible);
        Assert.Equal(0, shell.SimulationFraction, 6);
        Assert.Equal(-1, shell.ProgressSegment);
    }

    [AvaloniaFact]
    public void RegeneratingTheJobStopsAnyRunningSimulation()
    {
        // Otherwise a simulation keeps playing against a toolpath that no longer
        // exists, and the head wanders somewhere the job never goes.
        var shell = CreateShell();
        shell.PlaySimulationCommand.Execute(null);
        Assert.True(shell.IsSimulating);

        shell.Design.AddShape(PathShape.Ellipse(20, 20), shell.Design.Layers[0]);
        shell.RegenerateNow();

        Assert.False(shell.IsSimulating);
    }

    [AvaloniaFact]
    public void SelectionAndUndoSurviveARoundTrip()
    {
        var shell = CreateShell();
        var shape = shell.Design.Shapes[0];

        shell.SetSelection([shape], additive: false);
        Assert.True(shell.HasSelection);

        var before = shape.Bounds.MinX;
        shell.NudgeSelection(25, 0);
        Assert.Equal(before + 25, shape.Bounds.MinX, 6);

        Assert.True(shell.CanUndo);
        shell.UndoEditCommand.Execute(null);
        Assert.Equal(before, shape.Bounds.MinX, 6);

        Assert.True(shell.CanRedo);
        shell.RedoEditCommand.Execute(null);
        Assert.Equal(before + 25, shape.Bounds.MinX, 6);
    }

    [AvaloniaFact]
    public void AddingTextProducesGeometryOnTheBed()
    {
        var shell = CreateShell();
        var countBefore = shell.Design.Shapes.Count;

        shell.TextInput = "BURN";
        shell.TextSizeMm = 25;
        shell.AddTextCommand.Execute(null);

        Assert.Equal(countBefore + 1, shell.Design.Shapes.Count);

        var text = Assert.IsType<TextShape>(shell.Design.Shapes[^1]);
        Assert.NotEmpty(text.Outlines);
        Assert.True(text.Bounds.Width > 20, $"text is only {text.Bounds.Width:0.#} mm wide");

        // And it lands on the bed rather than off the edge.
        Assert.True(text.Bounds.MinX >= 0);
        Assert.True(text.Bounds.MaxX <= shell.SelectedMachine.BedWidthMm);
    }
}

/// <summary>
/// Job monitoring with a camera. §23 asks that the camera can stay active while a
/// job runs; the rule these tests protect is that the live view is only ever an
/// aid — it never gates or interferes with the job itself.
/// </summary>
public class JobMonitorTests : ShellTest
{
    private MainViewModel CreateShell()
    {
        return NewShell();
    }

    [AvaloniaFact]
    public void TheLiveViewIsHiddenWithNoCamera()
    {
        var shell = CreateShell();
        Assert.False(shell.ShowLiveView);
        Assert.Null(shell.LiveView);
    }

    [AvaloniaFact]
    public async Task ConnectingTheSyntheticCameraProducesFrames()
    {
        var shell = CreateShell();
        shell.RefreshCamerasCommand.Execute(null);

        await shell.ConnectCameraCommand.ExecuteAsync(null);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (shell.LiveView is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(shell.IsCameraLive);
        Assert.NotNull(shell.LiveView);

        // Shown only while a job is running — a live view of an idle bed is clutter.
        Assert.False(shell.ShowLiveView);

        await shell.DisconnectCameraCommand.ExecuteAsync(null);
        Assert.Null(shell.LiveView);
    }

    [AvaloniaFact]
    public async Task AnUncalibratedCameraStillGivesALiveViewButNoBedOverlay()
    {
        // Watching for a flare-up does not need millimetre accuracy; placing
        // artwork does. The two must not be conflated.
        var shell = CreateShell();
        shell.RefreshCamerasCommand.Execute(null);
        await shell.ConnectCameraCommand.ExecuteAsync(null);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (shell.LiveView is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.False(shell.IsCalibrated);
        Assert.NotNull(shell.LiveView);
        Assert.Null(shell.BedImage);

        await shell.DisconnectCameraCommand.ExecuteAsync(null);
    }
}
