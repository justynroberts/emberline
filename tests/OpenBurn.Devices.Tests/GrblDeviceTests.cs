using OpenBurn.Core.Geometry;
using OpenBurn.Core.Jobs;
using OpenBurn.Core.Machines;
using OpenBurn.Devices;
using OpenBurn.GCode.Grbl;
using OpenBurn.Transport;
using OpenBurn.VirtualLaser;
using Xunit;

namespace OpenBurn.Devices.Tests;

/// <summary>
/// The full stack — device, transport, protocol, job engine — against the virtual
/// laser. These are the tests that would otherwise require plugging in hardware.
/// </summary>
public class GrblDeviceTests : IAsyncLifetime
{
    private GrblDevice _device = null!;
    private VirtualTransport _transport = null!;

    public async Task InitializeAsync()
    {
        var profile = MachineProfile.Virtual() with { BedWidthMm = 200, BedHeightMm = 200 };
        _device = new GrblDevice(profile) { StatusPollHz = 20 };
        // Run the simulated machine far faster than real time so a full job
        // finishes inside a test rather than inside a coffee break.
        _transport = new VirtualTransport(new VirtualLaserOptions { BedWidthMm = 200, BedHeightMm = 200 }, realTimeScale: 200);
        await _device.ConnectAsync(_transport);
    }

    public async Task DisposeAsync() => await _device.DisposeAsync();

    private async Task WaitUntil(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(15);
        }
        Assert.Fail($"Condition not met within {timeoutMs} ms.");
    }

    [Fact]
    public async Task ConnectingReadsTheControllerSettings()
    {
        Assert.Equal(ConnectionState.Connected, _device.Connection);
        await WaitUntil(() => _device.Settings.Count > 20);

        // $30 is the one that matters most: it is what every power percentage scales to.
        Assert.True(_device.Settings.ContainsKey(30));
        Assert.Equal(1000, _device.Settings[30]);
        Assert.Equal(1, _device.Settings[32]); // laser mode
    }

    [Fact]
    public async Task StatusPollingProducesLiveReports()
    {
        await WaitUntil(() => _device.Status.State != MachineState.Disconnected);
        Assert.Equal(MachineState.Idle, _device.Status.State);
        Assert.NotNull(_device.Status.Buffer);
    }

    [Fact]
    public async Task ARealJobRunsToCompletion()
    {
        // A 30 mm square with a lead-in, exactly as the CAM layer would emit it.
        string[] lines =
        [
            "G21", "G90", "M4S0", "F2000",
            "G0X20Y20",
            "G1X50Y20S700", "G1X50Y50", "G1X20Y50", "G1X20Y20",
            "M5", "G0X0Y0",
        ];

        var job = new JobDefinition
        {
            Name = "square",
            Lines = lines,
            Bounds = new Rect2(20, 20, 50, 50),
            EstimatedDuration = TimeSpan.FromSeconds(4),
        };

        await _device.StartJobAsync(job);
        await WaitUntil(() => _device.JobState.IsTerminal(), 20000);

        Assert.Equal(JobState.Completed, _device.JobState);
        Assert.Equal(lines.Length, _device.Progress.LinesAcknowledged);
        Assert.Equal(1.0, _device.Progress.Fraction, 3);

        _transport.Controller.RunToIdle();
        Assert.Equal(4, _transport.Controller.BurnedSegments.Count);
        Assert.Equal(120, _transport.Controller.BurnDistanceMm, 2);
    }

    [Fact]
    public async Task ConsoleCommandsDuringAJobDoNotStealTheStreamsAcknowledgements()
    {
        // This is the failure that silently stalls a job: an out-of-band command
        // consumes an `ok` the streamer was waiting for, and streaming deadlocks.
        var lines = new List<string> { "G21", "G90", "M4S200", "F3000" };
        for (var i = 0; i < 400; i++) lines.Add($"G1X{10 + i % 100}Y{10 + i / 100}");
        lines.Add("M5");

        var job = new JobDefinition { Name = "long", Lines = lines, EstimatedDuration = TimeSpan.FromSeconds(20) };
        await _device.StartJobAsync(job);

        await WaitUntil(() => _device.Progress.LinesAcknowledged > 20, 10000);

        // Interleave console traffic with the running stream.
        for (var i = 0; i < 5; i++)
        {
            await _device.SendRawAsync("$G");
            await Task.Delay(30);
        }

        await WaitUntil(() => _device.JobState.IsTerminal(), 30000);

        Assert.Equal(JobState.Completed, _device.JobState);
        Assert.Equal(lines.Count, _device.Progress.LinesAcknowledged);
    }

    [Fact]
    public async Task PauseAndResumeReachCompletion()
    {
        var lines = new List<string> { "G21", "G90", "M4S300", "F2000" };
        for (var i = 0; i < 200; i++) lines.Add($"G1X{20 + i % 50}Y{20 + i / 50}");

        var job = new JobDefinition { Name = "pausable", Lines = lines, EstimatedDuration = TimeSpan.FromSeconds(10) };
        await _device.StartJobAsync(job);

        await WaitUntil(() => _device.Progress.LinesAcknowledged > 10, 10000);
        await _device.PauseJobAsync();
        Assert.Equal(JobState.Paused, _device.JobState);

        var atPause = _device.Progress.LinesAcknowledged;
        await Task.Delay(300);

        // Only what was already inside the controller may finish while paused.
        Assert.True(_device.Progress.LinesAcknowledged - atPause <= 20,
            $"{_device.Progress.LinesAcknowledged - atPause} lines completed during the pause");

        await _device.ResumeJobAsync();
        await WaitUntil(() => _device.JobState.IsTerminal(), 30000);
        Assert.Equal(JobState.Completed, _device.JobState);
    }

    [Fact]
    public async Task StoppingAJobRecordsWhereToResumeFrom()
    {
        var lines = new List<string> { "G21", "G90", "M4S300", "F1500" };
        for (var i = 0; i < 300; i++) lines.Add($"G1X{20 + i % 50}Y{20 + i / 50}");

        var job = new JobDefinition { Name = "stoppable", Lines = lines, EstimatedDuration = TimeSpan.FromSeconds(15) };
        await _device.StartJobAsync(job);

        await WaitUntil(() => _device.Progress.LinesAcknowledged > 15, 10000);
        await _device.StopJobAsync();

        Assert.Equal(JobState.Cancelled, _device.JobState);
        Assert.True(_device.ResumeLine > 0, "a stopped job must record a resume point");
        Assert.False(_device.IsHomed, "position is lost after a stop, so the machine must not claim to be homed");
    }

    [Fact]
    public async Task JoggingMovesTheMachine()
    {
        await WaitUntil(() => _device.Status.State == MachineState.Idle);

        await _device.JogAsync(25, 15, 0, 3000);
        await WaitUntil(() => _transport.Controller.PositionX > 24.9, 10000);

        Assert.Equal(25, _transport.Controller.PositionX, 1);
        Assert.Equal(15, _transport.Controller.PositionY, 1);
    }

    [Fact]
    public async Task FramingTracesTheBoundingBoxAndReturnsHome()
    {
        var outline = new List<Polyline>
        {
            new([new Vec2(30, 40), new Vec2(90, 40), new Vec2(90, 80), new Vec2(30, 80)], closed: true),
        };

        await _device.FrameAsync(outline, new FramingOptions
        {
            Mode = FramingMode.Rectangle,
            FeedRate = 4000,
            PowerPercent = 0,
        });

        _transport.Controller.RunToIdle();

        // The frame must end where it started, or the operator loses their reference.
        Assert.Equal(30, _transport.Controller.PositionX, 1);
        Assert.Equal(40, _transport.Controller.PositionY, 1);
        Assert.Empty(_transport.Controller.BurnedSegments);
    }

    [Fact]
    public async Task WritingASettingUpdatesTheController()
    {
        await WaitUntil(() => _device.Settings.Count > 20);

        await _device.WriteSettingAsync(30, 800);
        var reread = await _device.ReadSettingsAsync();

        Assert.Equal(800, reread[30]);
    }

    [Fact]
    public async Task AlarmDuringAJobFailsItAndOffersAResumePoint()
    {
        var profile = MachineProfile.Virtual() with
        {
            BedWidthMm = 100,
            BedHeightMm = 100,
            Capabilities = MachineCapabilities.Homing | MachineCapabilities.SoftLimits,
        };
        await using var device = new GrblDevice(profile) { StatusPollHz = 20 };
        var transport = new VirtualTransport(new VirtualLaserOptions
        {
            BedWidthMm = 100,
            BedHeightMm = 100,
            SoftLimitsEnabled = true,
        }, realTimeScale: 200);

        await device.ConnectAsync(transport);

        GrblCodeInfo? alarm = null;
        device.Fault += (info, isAlarm) => { if (isAlarm) alarm = info; };

        // The fifth line runs off the bed and must raise ALARM:2.
        string[] lines = ["G21", "G90", "M4S300", "F1500", "G1X50Y50", "G1X400Y50", "G1X60Y60"];
        await device.StartJobAsync(new JobDefinition { Name = "overrun", Lines = lines });

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline && device.JobState != JobState.Failed) await Task.Delay(15);

        Assert.Equal(JobState.Failed, device.JobState);
        Assert.NotNull(alarm);
        Assert.Equal(2, alarm!.Code);
        Assert.Contains("bed", alarm.Remedy, StringComparison.OrdinalIgnoreCase);
    }
}
