using System.Globalization;

namespace OpenBurn.GCode.Grbl;

/// <summary>
/// Turns controller output into <see cref="GrblMessage"/> values.
///
/// Stateful on purpose: GRBL only sends the work coordinate offset every few
/// reports, so the parser has to remember the last one to keep both machine and
/// work position correct on every single report. Getting this wrong is why some
/// senders show the work position jumping around.
/// </summary>
public sealed class GrblParser
{
    private Vec3 _workOffset = Vec3.Zero;

    public Vec3 WorkOffset => _workOffset;

    /// <summary>Call on connect and after a soft reset.</summary>
    public void Reset() => _workOffset = Vec3.Zero;

    public GrblMessage Parse(string rawLine)
    {
        var raw = rawLine.Trim();
        if (raw.Length == 0) return new GrblMessage.Unknown(raw);

        if (raw == "ok") return new GrblMessage.Ok();

        if (raw.StartsWith("error:", StringComparison.Ordinal))
        {
            var code = ParseInt(raw.AsSpan(6));
            return new GrblMessage.Error(code, GrblCodes.DescribeError(code), raw);
        }

        if (raw.StartsWith("ALARM:", StringComparison.Ordinal))
        {
            var code = ParseInt(raw.AsSpan(6));
            return new GrblMessage.Alarm(code, GrblCodes.DescribeAlarm(code), raw);
        }

        if (raw[0] == '<' && raw[^1] == '>') return new GrblMessage.Status(ParseStatus(raw), raw);

        if (raw.StartsWith("Grbl ", StringComparison.Ordinal))
        {
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new GrblMessage.Welcome(parts.Length > 1 ? parts[1] : "unknown", raw);
        }

        if (raw[0] == '$')
        {
            var eq = raw.IndexOf('=');
            if (eq > 1 &&
                int.TryParse(raw.AsSpan(1, eq - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var key) &&
                double.TryParse(raw.AsSpan(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return new GrblMessage.Setting(key, value, raw);
            }
        }

        if (raw[0] == '>')
        {
            var idx = raw.LastIndexOf(':');
            var line = idx > 0 ? raw[1..idx] : raw[1..];
            return new GrblMessage.StartupLine(line, raw.EndsWith(":ok", StringComparison.Ordinal), raw);
        }

        if (raw[0] == '[' && raw[^1] == ']')
        {
            var body = raw[1..^1];

            if (body.StartsWith("MSG:", StringComparison.Ordinal)) return new GrblMessage.Feedback(body[4..], raw);

            if (body.StartsWith("GC:", StringComparison.Ordinal))
            {
                return new GrblMessage.GcodeState(body[3..].Split(' ', StringSplitOptions.RemoveEmptyEntries), raw);
            }

            if (body.StartsWith("PRB:", StringComparison.Ordinal))
            {
                var payload = body[4..];
                var colon = payload.LastIndexOf(':');
                var coords = colon >= 0 ? payload[..colon] : payload;
                var ok = colon >= 0 && payload[(colon + 1)..] == "1";
                return new GrblMessage.Probe(ParseTriple(coords), ok, raw);
            }

            if (body.StartsWith("VER:", StringComparison.Ordinal)) return new GrblMessage.FirmwareInfo(body[4..], null, raw);
            if (body.StartsWith("OPT:", StringComparison.Ordinal)) return new GrblMessage.FirmwareInfo(string.Empty, body[4..], raw);

            var sep = body.IndexOf(':');
            if (sep > 0)
            {
                var name = body[..sep];
                if (name is "G54" or "G55" or "G56" or "G57" or "G58" or "G59" or "G28" or "G30" or "G92" or "TLO")
                {
                    return new GrblMessage.Offset(name, ParseTriple(body[(sep + 1)..]), raw);
                }
            }

            return new GrblMessage.Feedback(body, raw);
        }

        return new GrblMessage.Unknown(raw);
    }

    private GrblStatus ParseStatus(string raw)
    {
        var body = raw[1..^1];
        var fields = body.Split('|');

        var stateField = fields.Length > 0 ? fields[0] : "Idle";
        int? subState = null;
        var colon = stateField.IndexOf(':');
        if (colon >= 0)
        {
            if (int.TryParse(stateField.AsSpan(colon + 1), out var sub)) subState = sub;
            stateField = stateField[..colon];
        }

        var state = stateField switch
        {
            "Idle" => MachineState.Idle,
            "Run" => MachineState.Run,
            "Hold" => MachineState.Hold,
            "Jog" => MachineState.Jog,
            "Alarm" => MachineState.Alarm,
            "Door" => MachineState.Door,
            "Check" => MachineState.Check,
            "Home" => MachineState.Home,
            "Sleep" => MachineState.Sleep,
            _ => MachineState.Idle,
        };

        Vec3? mpos = null;
        Vec3? wpos = null;
        BufferState? buffer = null;
        double feed = 0;
        double spindle = 0;
        var overrides = Overrides.Default;
        var accessories = string.Empty;
        var pins = string.Empty;
        int? lineNumber = null;

        for (var i = 1; i < fields.Length; i++)
        {
            var f = fields[i];
            var c = f.IndexOf(':');
            if (c < 0) continue;
            var key = f[..c];
            var val = f[(c + 1)..];

            switch (key)
            {
                case "MPos": mpos = ParseTriple(val); break;
                case "WPos": wpos = ParseTriple(val); break;
                case "WCO": _workOffset = ParseTriple(val); break;
                case "Bf":
                {
                    var parts = val.Split(',');
                    buffer = new BufferState(ParseInt(parts.Length > 0 ? parts[0] : "0"),
                                             ParseInt(parts.Length > 1 ? parts[1] : "0"));
                    break;
                }
                case "FS":
                {
                    var parts = val.Split(',');
                    feed = ParseDouble(parts.Length > 0 ? parts[0] : "0");
                    spindle = ParseDouble(parts.Length > 1 ? parts[1] : "0");
                    break;
                }
                case "F": feed = ParseDouble(val); break;
                case "Ov":
                {
                    var parts = val.Split(',');
                    overrides = new Overrides(
                        parts.Length > 0 ? ParseInt(parts[0]) : 100,
                        parts.Length > 1 ? ParseInt(parts[1]) : 100,
                        parts.Length > 2 ? ParseInt(parts[2]) : 100);
                    break;
                }
                case "A": accessories = val; break;
                case "Pn": pins = val; break;
                case "Ln": lineNumber = ParseInt(val); break;
            }
        }

        // GRBL reports either MPos or WPos, never both. Derive the other.
        if (mpos is { } m && wpos is null) wpos = m - _workOffset;
        else if (wpos is { } w && mpos is null) mpos = w + _workOffset;

        return new GrblStatus
        {
            State = state,
            SubState = subState,
            MachinePosition = mpos ?? Vec3.Zero,
            WorkPosition = wpos ?? Vec3.Zero,
            WorkOffset = _workOffset,
            Buffer = buffer,
            Feed = feed,
            Spindle = spindle,
            Overrides = overrides,
            Accessories = accessories,
            Pins = pins,
            LineNumber = lineNumber,
        };
    }

    private static Vec3 ParseTriple(string s)
    {
        var parts = s.Split(',');
        return new Vec3(
            parts.Length > 0 ? ParseDouble(parts[0]) : 0,
            parts.Length > 1 ? ParseDouble(parts[1]) : 0,
            parts.Length > 2 ? ParseDouble(parts[2]) : 0);
    }

    private static double ParseDouble(ReadOnlySpan<char> s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int ParseInt(ReadOnlySpan<char> s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

/// <summary>
/// Splits arbitrary transport chunks into complete lines. TCP in particular will
/// happily hand you half a status report; without this the parser sees garbage
/// every few hundred packets.
/// </summary>
public sealed class LineAssembler
{
    private readonly System.Text.StringBuilder _buffer = new();

    public string Pending => _buffer.ToString();

    public List<string> Push(string chunk)
    {
        var lines = new List<string>();
        foreach (var ch in chunk)
        {
            if (ch is '\n' or '\r')
            {
                if (_buffer.Length > 0)
                {
                    lines.Add(_buffer.ToString());
                    _buffer.Clear();
                }
            }
            else
            {
                _buffer.Append(ch);
            }
        }
        return lines;
    }

    public void Reset() => _buffer.Clear();
}
