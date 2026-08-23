using OpenBurn.GCode.Grbl;
using OpenBurn.VirtualLaser;
using Xunit;

namespace OpenBurn.Transport.Tests;

/// <summary>
/// The streamer and the virtual controller, wired together exactly as they are in
/// the real application. This is the test that matters most in the whole suite:
/// if character counting is wrong, every job on every machine is wrong.
/// </summary>
public class StreamingIntegrationTests
{
    /// <summary>Wires a streamer to a simulator and runs a job to completion.</summary>
    private static (GcodeStreamer Streamer, VirtualGrblController Machine) RunJob(
        IReadOnlyList<string> lines,
        VirtualLaserOptions? options = null,
        bool stopOnError = true)
    {
        var machine = new VirtualGrblController(options);
        GcodeStreamer? streamer = null;

        machine.LineEmitted += line =>
        {
            if (streamer is null) return;
            if (line == "ok") streamer.Acknowledge();
            else if (line.StartsWith("error:", StringComparison.Ordinal))
            {
                streamer.AcknowledgeError(int.Parse(line[6..]));
            }
        };

        streamer = new GcodeStreamer(machine.Write, GcodeStreamer.DefaultRxBufferSize, stopOnError);
        streamer.Load(lines);
        streamer.Start();

        // The simulator only completes moves when time advances; keep ticking
        // until both the stream and the planner have drained.
        var guard = 0;
        while (streamer.State is StreamState.Running or StreamState.Paused or StreamState.Stopping && guard++ < 5_000_000)
        {
            machine.Tick(0.01);
        }
        machine.RunToIdle();

        return (streamer, machine);
    }

    [Fact]
    public void SimpleJobCompletesAndAcknowledgesEveryLine()
    {
        string[] lines = ["G21", "G90", "M4S0", "G0X10Y10", "G1X50Y10S500F1200", "G1X50Y50", "M5", "G0X0Y0"];

        var (streamer, machine) = RunJob(lines);

        Assert.Equal(StreamState.Completed, streamer.State);
        Assert.Equal(lines.Length, streamer.Acknowledged);
        Assert.Empty(streamer.Errors);
        Assert.False(machine.BufferOverflowed);
    }

    [Fact]
    public void ReceiveBufferIsNeverOverrun()
    {
        // A dense raster-shaped job: many short lines, which is exactly the workload
        // that overruns a naive streamer.
        var lines = new List<string> { "G21", "G90", "M4S0", "F3000" };
        for (var row = 0; row < 200; row++)
        {
            lines.Add($"G0X0Y{row * 0.1:0.###}");
            for (var i = 0; i < 20; i++) lines.Add($"G1X{i * 2 + 2}S{200 + i * 10}");
        }
        lines.Add("M5");

        var (streamer, machine) = RunJob(lines);

        Assert.Equal(StreamState.Completed, streamer.State);
        Assert.Equal(lines.Count, streamer.Acknowledged);
        Assert.False(machine.BufferOverflowed);

        // Both sides must agree the buffer stayed under the limit.
        Assert.True(streamer.PeakBytesInFlight < GcodeStreamer.DefaultRxBufferSize,
            $"streamer peak {streamer.PeakBytesInFlight} reached the buffer size");
        Assert.True(machine.PeakRxBytes <= GcodeStreamer.DefaultRxBufferSize,
            $"controller peak {machine.PeakRxBytes} exceeded the buffer size");
    }

    [Fact]
    public void StreamerKeepsBufferMeaningfullyFull()
    {
        // The entire reason for character counting: a send-and-wait streamer peaks
        // at one line in flight. We should be keeping many lines queued.
        var lines = new List<string> { "G21", "G90", "M4S300", "F3000" };
        for (var i = 0; i < 500; i++) lines.Add($"G1X{i % 100}Y{i / 100}");

        var (streamer, _) = RunJob(lines);

        Assert.Equal(StreamState.Completed, streamer.State);
        Assert.True(streamer.PeakBytesInFlight > 90,
            $"only reached {streamer.PeakBytesInFlight} bytes in flight — the buffer is being under-filled");
    }

    [Fact]
    public void ErrorStopsTheJobWhenStopOnErrorIsSet()
    {
        // error:20 is raised by the simulator every 5th line.
        var lines = new List<string>();
        for (var i = 0; i < 40; i++) lines.Add($"G1X{i}F1000S100");

        var (streamer, _) = RunJob(lines, new VirtualLaserOptions { FaultEveryNLines = 5 }, stopOnError: true);

        Assert.Equal(StreamState.Faulted, streamer.State);
        Assert.NotEmpty(streamer.Errors);
        Assert.Equal(20, streamer.Errors[0].Code);
        Assert.Equal(4, streamer.Errors[0].LineIndex);
        Assert.True(streamer.Acknowledged < lines.Count,
            "the job should have stopped early rather than streaming to the end");
    }

    [Fact]
    public void ErrorsAreCollectedWhenStopOnErrorIsClear()
    {
        var lines = new List<string>();
        for (var i = 0; i < 40; i++) lines.Add($"G1X{i}F1000S100");

        var (streamer, _) = RunJob(lines, new VirtualLaserOptions { FaultEveryNLines = 5 }, stopOnError: false);

        Assert.Equal(StreamState.Completed, streamer.State);
        Assert.Equal(lines.Count, streamer.Acknowledged);
        Assert.True(streamer.Errors.Count >= 7, $"expected several collected errors, got {streamer.Errors.Count}");
    }

    [Fact]
    public void PauseHoldsTheStreamAndResumeFinishesIt()
    {
        var machine = new VirtualGrblController();
        GcodeStreamer? streamer = null;
        machine.LineEmitted += line =>
        {
            if (streamer is null) return;
            if (line == "ok") streamer.Acknowledge();
            else if (line.StartsWith("error:", StringComparison.Ordinal)) streamer.AcknowledgeError(int.Parse(line[6..]));
        };

        var lines = new List<string>();
        for (var i = 0; i < 300; i++) lines.Add($"G1X{i % 50}Y{i / 50}F2000S400");

        streamer = new GcodeStreamer(machine.Write);
        streamer.Load(lines);
        streamer.Start();

        for (var i = 0; i < 5; i++) machine.Tick(0.01);
        streamer.Pause();

        var atPause = streamer.Acknowledged;
        for (var i = 0; i < 200; i++) machine.Tick(0.01);

        // Only the lines already inside the controller may finish while paused.
        Assert.Equal(StreamState.Paused, streamer.State);
        Assert.True(streamer.Acknowledged - atPause <= 16,
            $"{streamer.Acknowledged - atPause} lines completed while paused — more than one buffer's worth");

        streamer.Resume();
        var guard = 0;
        while (streamer.State == StreamState.Running && guard++ < 1_000_000) machine.Tick(0.01);

        Assert.Equal(StreamState.Completed, streamer.State);
        Assert.Equal(lines.Count, streamer.Acknowledged);
    }

    [Fact]
    public void TheMachineActuallyBurnsTheCommandedGeometry()
    {
        // A 40 mm square at S800. Assert the simulator moved where it was told,
        // which proves the whole chain end to end rather than just the handshake.
        string[] lines =
        [
            "G21", "G90", "M4S0", "F1500",
            "G0X10Y10",
            "G1X50Y10S800", "G1X50Y50", "G1X10Y50", "G1X10Y10",
            "M5", "G0X0Y0",
        ];

        var (streamer, machine) = RunJob(lines);

        Assert.Equal(StreamState.Completed, streamer.State);
        Assert.Equal(4, machine.BurnedSegments.Count);
        Assert.Equal(160, machine.BurnDistanceMm, 3);
        Assert.All(machine.BurnedSegments, s => Assert.Equal(800, s.Power, 3));
        Assert.Equal(0, machine.PositionX, 3);
        Assert.Equal(0, machine.PositionY, 3);
    }

    [Fact]
    public void GcodeIsLockedOutUntilTheAlarmIsCleared()
    {
        var machine = new VirtualGrblController(new VirtualLaserOptions { StartInAlarm = true });
        machine.DrainOutput();

        machine.Write("G1X10F600\n");
        Assert.Contains("error:9", machine.DrainOutput());

        machine.Write("$X\n");
        var unlock = machine.DrainOutput();
        Assert.Contains(unlock, l => l.Contains("Unlocked", StringComparison.Ordinal));
        Assert.Contains("ok", unlock);

        machine.Write("G1X10F600\n");
        Assert.Contains("ok", machine.DrainOutput());
    }

    [Fact]
    public void SoftLimitsRaiseAlarmTwoInsteadOfMoving()
    {
        var machine = new VirtualGrblController(new VirtualLaserOptions
        {
            SoftLimitsEnabled = true,
            BedWidthMm = 100,
            BedHeightMm = 100,
        });
        machine.DrainOutput();

        machine.Write("G90\nG1X500Y10F1000\n");
        var output = machine.DrainOutput();

        Assert.Contains("ALARM:2", output);
        machine.RunToIdle();
        Assert.Equal(0, machine.PositionX, 3);
    }
}
