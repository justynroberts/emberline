using OpenBurn.GCode.Grbl;
using Xunit;

namespace OpenBurn.GCode.Tests;

public class GrblParserTests
{
    private readonly GrblParser _parser = new();

    [Fact]
    public void ParsesOkAndErrorAndAlarm()
    {
        Assert.IsType<GrblMessage.Ok>(_parser.Parse("ok"));

        var error = Assert.IsType<GrblMessage.Error>(_parser.Parse("error:9"));
        Assert.Equal(9, error.Code);
        Assert.Contains("$X", error.Info.Remedy, StringComparison.Ordinal);

        var alarm = Assert.IsType<GrblMessage.Alarm>(_parser.Parse("ALARM:1"));
        Assert.Equal(1, alarm.Code);
        Assert.Contains("Hard limit", alarm.Info.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParsesAFullStatusReport()
    {
        var msg = Assert.IsType<GrblMessage.Status>(
            _parser.Parse("<Run|MPos:12.500,34.000,0.000|Bf:14,120|FS:1500,650|Ov:110,100,90|A:S|Pn:XP|Ln:42>"));

        var s = msg.Value;
        Assert.Equal(MachineState.Run, s.State);
        Assert.Equal(12.5, s.MachinePosition.X, 4);
        Assert.Equal(34.0, s.MachinePosition.Y, 4);
        Assert.Equal(14, s.Buffer!.Value.PlannerBlocks);
        Assert.Equal(120, s.Buffer!.Value.RxBytes);
        Assert.Equal(1500, s.Feed, 4);
        Assert.Equal(650, s.Spindle, 4);
        Assert.Equal(110, s.Overrides.Feed);
        Assert.Equal(90, s.Overrides.Spindle);
        Assert.Equal("S", s.Accessories);
        Assert.Equal("XP", s.Pins);
        Assert.Equal(42, s.LineNumber);
    }

    [Fact]
    public void RemembersWorkOffsetAcrossReports()
    {
        // GRBL only sends WCO occasionally. Forgetting it is why some senders show
        // the work position jumping about.
        _parser.Parse("<Idle|MPos:100.000,50.000,0.000|WCO:10.000,20.000,0.000>");
        var later = Assert.IsType<GrblMessage.Status>(_parser.Parse("<Idle|MPos:110.000,60.000,0.000|FS:0,0>"));

        Assert.Equal(100, later.Value.WorkPosition.X, 4);
        Assert.Equal(40, later.Value.WorkPosition.Y, 4);
        Assert.Equal(110, later.Value.MachinePosition.X, 4);
    }

    [Fact]
    public void DerivesMachinePositionWhenOnlyWorkPositionIsReported()
    {
        _parser.Parse("<Idle|MPos:0.000,0.000,0.000|WCO:5.000,7.000,0.000>");
        var msg = Assert.IsType<GrblMessage.Status>(_parser.Parse("<Idle|WPos:10.000,10.000,0.000>"));

        Assert.Equal(15, msg.Value.MachinePosition.X, 4);
        Assert.Equal(17, msg.Value.MachinePosition.Y, 4);
    }

    [Fact]
    public void ParsesHoldSubState()
    {
        var msg = Assert.IsType<GrblMessage.Status>(_parser.Parse("<Hold:0|MPos:1.000,2.000,0.000>"));
        Assert.Equal(MachineState.Hold, msg.Value.State);
        Assert.Equal(0, msg.Value.SubState);
    }

    [Theory]
    [InlineData("$32=1", 32, 1.0)]
    [InlineData("$110=6000.000", 110, 6000.0)]
    [InlineData("$11=0.010", 11, 0.01)]
    public void ParsesSettings(string line, int key, double value)
    {
        var msg = Assert.IsType<GrblMessage.Setting>(_parser.Parse(line));
        Assert.Equal(key, msg.Key);
        Assert.Equal(value, msg.Value, 5);
    }

    [Fact]
    public void ParsesBracketMessages()
    {
        Assert.IsType<GrblMessage.Feedback>(_parser.Parse("[MSG:'$H'|'$X' to unlock]"));

        var gc = Assert.IsType<GrblMessage.GcodeState>(_parser.Parse("[GC:G0 G54 G17 G21 G90 G94 M5 M9 T0 F0 S0]"));
        Assert.Contains("G21", gc.Modals);

        var probe = Assert.IsType<GrblMessage.Probe>(_parser.Parse("[PRB:1.000,2.000,3.000:1]"));
        Assert.True(probe.Success);
        Assert.Equal(2, probe.Position.Y, 4);

        var offset = Assert.IsType<GrblMessage.Offset>(_parser.Parse("[G54:10.000,20.000,0.000]"));
        Assert.Equal("G54", offset.Name);
        Assert.Equal(20, offset.Position.Y, 4);
    }

    [Fact]
    public void ParsesWelcomeBanner()
    {
        var msg = Assert.IsType<GrblMessage.Welcome>(_parser.Parse("Grbl 1.1h ['$' for help]"));
        Assert.Equal("1.1h", msg.Version);
    }
}

public class LineAssemblerTests
{
    [Fact]
    public void ReassemblesLinesSplitAcrossChunks()
    {
        // TCP will happily deliver half a status report.
        var assembler = new LineAssembler();

        Assert.Empty(assembler.Push("<Idle|MPos:0.0"));
        Assert.Equal("<Idle|MPos:0.000,0.000,0.000>", Assert.Single(assembler.Push("00,0.000,0.000>\r\n")));
    }

    [Fact]
    public void HandlesAllLineTerminators()
    {
        var assembler = new LineAssembler();
        var lines = assembler.Push("ok\r\nok\nok\rok\n");
        Assert.Equal(4, lines.Count);
        Assert.All(lines, l => Assert.Equal("ok", l));
    }

    [Fact]
    public void HoldsBackAPartialLine()
    {
        var assembler = new LineAssembler();
        Assert.Single(assembler.Push("ok\nerr"));
        Assert.Equal("err", assembler.Pending);
    }
}
