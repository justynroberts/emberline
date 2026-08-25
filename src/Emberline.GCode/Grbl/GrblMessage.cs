namespace Emberline.GCode.Grbl;

/// <summary>Every line the controller can emit, as a closed set.</summary>
public abstract record GrblMessage(string Raw)
{
    public sealed record Ok() : GrblMessage("ok");
    public sealed record Error(int Code, GrblCodeInfo Info, string RawLine) : GrblMessage(RawLine);
    public sealed record Alarm(int Code, GrblCodeInfo Info, string RawLine) : GrblMessage(RawLine);
    public sealed record Status(GrblStatus Value, string RawLine) : GrblMessage(RawLine);
    public sealed record Welcome(string Version, string RawLine) : GrblMessage(RawLine);
    public sealed record Setting(int Key, double Value, string RawLine) : GrblMessage(RawLine);
    public sealed record Feedback(string Text, string RawLine) : GrblMessage(RawLine);
    public sealed record GcodeState(IReadOnlyList<string> Modals, string RawLine) : GrblMessage(RawLine);
    public sealed record Probe(Vec3 Position, bool Success, string RawLine) : GrblMessage(RawLine);
    public sealed record Offset(string Name, Vec3 Position, string RawLine) : GrblMessage(RawLine);
    public sealed record FirmwareInfo(string Value, string? Options, string RawLine) : GrblMessage(RawLine);
    public sealed record StartupLine(string Line, bool Succeeded, string RawLine) : GrblMessage(RawLine);
    public sealed record Unknown(string RawLine) : GrblMessage(RawLine);
}
