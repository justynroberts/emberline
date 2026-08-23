using OpenBurn.GCode;
using Xunit;

namespace OpenBurn.GCode.Tests;

public class GcodeInterpreterTests
{
    [Fact]
    public void InterpretsASimpleSquare()
    {
        var tp = GcodeInterpreter.Interpret("""
            G21 G90
            M4 S500
            G0 X10 Y10
            G1 X50 Y10 F1000
            G1 X50 Y50
            G1 X10 Y50
            G1 X10 Y10
            M5
            """);

        Assert.True(tp.UsesLaser);
        Assert.Equal(500, tp.MaxSpindleSeen, 3);
        Assert.Equal(160, tp.CutLengthMm, 3);
        Assert.Equal(10, tp.BurnBounds.MinX, 3);
        Assert.Equal(50, tp.BurnBounds.MaxY, 3);
    }

    [Fact]
    public void ConvertsInchesToMillimetres()
    {
        var tp = GcodeInterpreter.Interpret("G20 G90\nM3 S1000\nG1 X1 Y0 F10");
        Assert.True(tp.IsInches);
        Assert.Equal(25.4, tp.CutLengthMm, 3);
    }

    [Fact]
    public void HandlesRelativeMoves()
    {
        var tp = GcodeInterpreter.Interpret("G21 G91\nM4 S500\nG1 X10 F600\nG1 X10\nG1 Y10");
        Assert.Equal(30, tp.CutLengthMm, 3);
        Assert.Equal(20, tp.BurnBounds.MaxX, 3);
        Assert.Equal(10, tp.BurnBounds.MaxY, 3);
    }

    [Fact]
    public void ExpandsAnArcIntoChordsOfTheRightLength()
    {
        // Counter-clockwise from (10,0) to (0,10) is a quarter circle of radius 10,
        // so 10·π/2 ≈ 15.708 mm.
        var ccw = GcodeInterpreter.Interpret("G21 G90\nM4 S500\nG0 X10 Y0\nG3 X0 Y10 I-10 J0 F600");

        Assert.True(ccw.Count > 10, "the arc should be broken into many chords");
        Assert.Equal(15.708, ccw.CutLengthMm, 1);
        Assert.Equal(0, ccw.X1[^1], 3);
        Assert.Equal(10, ccw.Y1[^1], 3);

        // The same endpoints clockwise take the long way round: three quarters of
        // the circumference, 47.12 mm. Getting this backwards is a classic arc bug.
        var cw = GcodeInterpreter.Interpret("G21 G90\nM4 S500\nG0 X10 Y0\nG2 X0 Y10 I-10 J0 F600");
        Assert.Equal(47.124, cw.CutLengthMm, 1);
    }

    [Fact]
    public void ExpandsAFullCircleWhenEndpointsCoincide()
    {
        // Circumference of a radius-10 circle is 62.83 mm.
        var tp = GcodeInterpreter.Interpret("G21 G90\nM4 S500\nG0 X10 Y0\nG3 X10 Y0 I-10 J0 F600");
        Assert.Equal(62.83, tp.CutLengthMm, 1);
    }

    [Fact]
    public void SolvesRadiusFormatArcs()
    {
        var tp = GcodeInterpreter.Interpret("G21 G90\nM4 S500\nG0 X0 Y0\nG2 X10 Y10 R10 F600");
        Assert.Empty(tp.Warnings);
        Assert.Equal(10, tp.X1[^1], 2);
        Assert.Equal(10, tp.Y1[^1], 2);
    }

    [Fact]
    public void WarnsAboutAnImpossibleArcRatherThanCrashing()
    {
        // R is far too small to span the endpoints.
        var tp = GcodeInterpreter.Interpret("G21 G90\nM4 S500\nG0 X0 Y0\nG2 X100 Y0 R1 F600");
        Assert.NotEmpty(tp.Warnings);
        Assert.Equal(100, tp.X1[^1], 3);
    }

    [Fact]
    public void RapidsAreNotCountedAsCutting()
    {
        var tp = GcodeInterpreter.Interpret("G21 G90\nM4 S500\nG0 X100 Y0\nG1 X110 Y0 F600");
        Assert.Equal(10, tp.CutLengthMm, 3);
        Assert.Equal(100, tp.TravelLengthMm, 3);
    }

    [Fact]
    public void MovesWithTheLaserOffAreTravelEvenWhenCommandedAsG1()
    {
        var tp = GcodeInterpreter.Interpret("G21 G90\nM5\nG1 X50 Y0 F600");
        Assert.Equal(0, tp.CutLengthMm, 3);
        Assert.Equal(50, tp.TravelLengthMm, 3);
    }

    [Fact]
    public void IgnoresCommentsInBothForms()
    {
        var tp = GcodeInterpreter.Interpret("G21 G90 ; set units\nM4 S500 (laser on)\nG1 X10 F600 ; move");
        Assert.Equal(10, tp.CutLengthMm, 3);
    }
}

public class TimeEstimatorTests
{
    [Fact]
    public void ALongStraightMoveApproachesLengthOverFeed()
    {
        // 300 mm at 600 mm/min is 30 s of cruise, plus a little accel and decel.
        var tp = GcodeInterpreter.Interpret("G21 G90\nM4 S500\nG1 X300 Y0 F600");
        var estimate = TimeEstimator.Estimate(tp, MachineLimits.Default);

        Assert.InRange(estimate.Total.TotalSeconds, 30, 31);
    }

    [Fact]
    public void ManyShortMovesTakeFarLongerThanLengthOverFeedSuggests()
    {
        // This is the whole reason the estimator exists: a raster is thousands of
        // short moves that never reach the commanded feed rate.
        var lines = new List<string> { "G21", "G90", "M4 S500", "F6000" };
        for (var i = 1; i <= 600; i++) lines.Add($"G1 X{i * 0.5:0.###} Y{(i % 2) * 0.1:0.###}");

        var tp = GcodeInterpreter.Interpret(lines);
        var estimate = TimeEstimator.Estimate(tp, MachineLimits.Default);

        var naive = tp.CutLengthMm / (6000 / 60.0);
        Assert.True(estimate.Total.TotalSeconds > naive * 1.2,
            $"estimate {estimate.Total.TotalSeconds:0.00}s should exceed the naive {naive:0.00}s");
    }

    [Fact]
    public void SegmentTimeHandlesTheTriangularCase()
    {
        // Too short to reach vMax: accelerate then immediately decelerate.
        var t = TimeEstimator.SegmentTime(length: 1, vIn: 0, vOut: 0, vMax: 1000, accel: 1000);
        // v = sqrt(a·d) = sqrt(1000) ≈ 31.6 mm/s peak, so t = 2·v/a ≈ 0.063 s.
        Assert.InRange(t, 0.06, 0.07);
    }

    [Fact]
    public void ZeroLengthSegmentsTakeNoTime()
    {
        Assert.Equal(0, TimeEstimator.SegmentTime(0, 0, 0, 1000, 1000));
    }

    [Fact]
    public void LowerAccelerationMakesTheSameJobTakeLonger()
    {
        var lines = new List<string> { "G21", "G90", "M4 S500", "F3000" };
        for (var i = 0; i < 200; i++) lines.Add($"G1 X{i % 20} Y{i / 20}");
        var tp = GcodeInterpreter.Interpret(lines);

        var fast = TimeEstimator.Estimate(tp, MachineLimits.Default with { AccelerationX = 3000, AccelerationY = 3000 });
        var slow = TimeEstimator.Estimate(tp, MachineLimits.Default with { AccelerationX = 300, AccelerationY = 300 });

        Assert.True(slow.Total > fast.Total, "a machine with less acceleration must take longer");
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(125, "2m 05s")]
    [InlineData(3725, "1h 02m")]
    public void FormatsDurationsReadably(int seconds, string expected) =>
        Assert.Equal(expected, TimeEstimator.Format(TimeSpan.FromSeconds(seconds)));
}

public class ToolpathSimulatorTests
{
    private static Toolpath Square() => GcodeInterpreter.Interpret("""
        G21 G90
        M4 S500
        G0 X10 Y10
        G1 X50 Y10 F1200
        G1 X50 Y50
        G1 X10 Y50
        G1 X10 Y10
        M5
        """);

    [Fact]
    public void TotalTimeMatchesTheEstimate()
    {
        // A preview that plays at a different speed from the prediction teaches the
        // operator to distrust both, so the two must come from the same numbers.
        var toolpath = Square();
        var estimate = TimeEstimator.Estimate(toolpath);
        var simulator = new ToolpathSimulator(toolpath);

        Assert.Equal(estimate.Total.TotalSeconds, simulator.Total.TotalSeconds, 6);
    }

    [Fact]
    public void StartsAtTheFirstPointAndEndsAtTheLast()
    {
        var toolpath = Square();
        var simulator = new ToolpathSimulator(toolpath);

        var start = simulator.At(TimeSpan.Zero);
        Assert.Equal(toolpath.X0[0], start.Position.X, 6);
        Assert.Equal(toolpath.Y0[0], start.Position.Y, 6);

        var end = simulator.At(simulator.Total);
        Assert.Equal(toolpath.X1[^1], end.Position.X, 6);
        Assert.Equal(toolpath.Y1[^1], end.Position.Y, 6);
    }

    [Fact]
    public void PositionAdvancesMonotonically()
    {
        var toolpath = Square();
        var simulator = new ToolpathSimulator(toolpath);

        var previousSegment = -1;
        for (var i = 0; i <= 100; i++)
        {
            var state = simulator.AtFraction(i / 100.0);
            Assert.True(state.SegmentIndex >= previousSegment,
                $"segment went backwards at {i}%: {state.SegmentIndex} after {previousSegment}");
            previousSegment = state.SegmentIndex;
        }
    }

    [Fact]
    public void ClampsOutsideTheJob()
    {
        var simulator = new ToolpathSimulator(Square());

        var before = simulator.At(TimeSpan.FromSeconds(-100));
        var after = simulator.At(TimeSpan.FromHours(10));

        Assert.Equal(TimeSpan.Zero, before.Elapsed);
        Assert.Equal(simulator.Total.TotalSeconds, after.Elapsed.TotalSeconds, 6);
    }

    [Fact]
    public void ReportsPowerAndRapidStateAlongTheWay()
    {
        var simulator = new ToolpathSimulator(Square());

        var states = Enumerable.Range(0, 200).Select(i => simulator.AtFraction(i / 199.0)).ToList();

        Assert.Contains(states, s => s.IsRapid);
        Assert.Contains(states, s => !s.IsRapid && s.PowerFraction > 0);
    }

    [Fact]
    public void FractionAtSegmentSpansZeroToOne()
    {
        var toolpath = Square();
        var simulator = new ToolpathSimulator(toolpath);

        Assert.Equal(0, simulator.FractionAtSegment(0), 9);
        Assert.Equal(1, simulator.FractionAtSegment(toolpath.Count), 9);
        Assert.InRange(simulator.FractionAtSegment(toolpath.Count / 2), 0, 1);
    }

    [Fact]
    public void AnEmptyToolpathIsHarmless()
    {
        var simulator = new ToolpathSimulator(GcodeInterpreter.Interpret(""));

        Assert.Equal(TimeSpan.Zero, simulator.Total);
        Assert.Equal(SimulationState.Start, simulator.At(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void DescribesWhatTheHeadIsDoing()
    {
        Assert.Equal("Rapid", ToolpathSimulator.Describe(new SimulationState(default, 0, 0, 0, true, default)));
        Assert.StartsWith("Engrave", ToolpathSimulator.Describe(new SimulationState(default, 0, 0.25, 0, false, default)));
        Assert.StartsWith("Cut", ToolpathSimulator.Describe(new SimulationState(default, 0, 0.9, 0, false, default)));
    }

    [Fact]
    public void ScrubbingALargeJobIsCheap()
    {
        // Binary search, not a linear walk: scrubbing must not get slower with job size.
        var lines = new List<string> { "G21", "G90", "M4 S500", "F3000" };
        for (var i = 0; i < 40_000; i++) lines.Add($"G1 X{i % 200} Y{i / 200}");

        var simulator = new ToolpathSimulator(GcodeInterpreter.Interpret(lines));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 2000; i++) simulator.AtFraction(i / 2000.0);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 250,
            $"2000 scrub queries took {stopwatch.ElapsedMilliseconds} ms");
    }
}
