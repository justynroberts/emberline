using System.Globalization;
using System.Text;

namespace Emberline.VirtualLaser;

/// <summary>
/// An in-process GRBL 1.1 controller.
///
/// This exists because hardware software needs far more testing than ordinary
/// desktop software, and you cannot put a laser in CI. It implements enough of
/// GRBL 1.1 to be indistinguishable from the real thing at the protocol level:
/// the 128-byte receive buffer, the planner block queue, character-counting
/// acknowledgement timing, real-time bytes, status reports, the settings table,
/// homing, alarms and soft limits.
///
/// Time is driven by <see cref="Tick"/> rather than a wall clock, so a test can
/// run a two-hour job in a millisecond and get exactly reproducible results.
/// </summary>
public sealed class VirtualGrblController
{
    private readonly VirtualLaserOptions _options;
    private readonly StringBuilder _rxLine = new();
    private readonly Queue<PlannedMove> _planner = new();
    private readonly Queue<string> _pendingLines = new();
    private readonly Dictionary<int, double> _settings;
    private readonly List<string> _pendingOutput = [];

    // Modal state
    private bool _absolute = true;
    private double _unitScale = 1.0;
    private int _motion;
    private double _feed;
    private double _spindle;
    private bool _laserOn;

    // Physical position, millimetres, machine coordinates
    private double _mx, _my, _mz;

    // Position after everything already queued. Moves are planned ahead of being
    // executed, so a queued move's start point is the planner head, not where the
    // head physically happens to be at the moment it was queued.
    private double _plannedX, _plannedY, _plannedZ;

    private PlannedMove? _active;
    private double _activeElapsed;

    private double _offsetX, _offsetY, _offsetZ;

    private MachineMode _state;
    private int _alarmCode;

    /// <summary>Bytes received but not yet acknowledged — the receive buffer occupancy.</summary>
    private int _rxBytesPending;

    private double _homingRemaining;
    private double _elapsed;

    public VirtualGrblController(VirtualLaserOptions? options = null)
    {
        _options = options ?? VirtualLaserOptions.Default;
        _state = _options.StartInAlarm ? MachineMode.Alarm : MachineMode.Idle;
        _alarmCode = _options.StartInAlarm ? 3 : 0;
        _settings = DefaultSettings();
        EmitWelcome();
    }

    private enum MachineMode { Idle, Run, Hold, Jog, Alarm, Door, Check, Home, Sleep }

    private readonly record struct PlannedMove(
        double TargetX, double TargetY, double TargetZ,
        double StartX, double StartY, double StartZ,
        double DurationSeconds, double Spindle, bool Rapid)
    {
        public double Length => Math.Sqrt(
            (TargetX - StartX) * (TargetX - StartX) +
            (TargetY - StartY) * (TargetY - StartY) +
            (TargetZ - StartZ) * (TargetZ - StartZ));
    }

    /// <summary>Raised for every line the controller emits, without the terminator.</summary>
    public event Action<string>? LineEmitted;

    /// <summary>Set when the receive buffer was overrun — a streaming bug on our side, never the machine's.</summary>
    public bool BufferOverflowed { get; private set; }

    public int PeakRxBytes { get; private set; }
    public int LinesProcessed { get; private set; }

    /// <summary>Total distance travelled with the beam on, for asserting a job actually burned something.</summary>
    public double BurnDistanceMm { get; private set; }
    public double TravelDistanceMm { get; private set; }

    public double PositionX => _mx;
    public double PositionY => _my;
    public double PositionZ => _mz;
    public bool IsIdle => _state == MachineMode.Idle && _planner.Count == 0 && _active is null && _pendingLines.Count == 0;
    public string StateName => _state.ToString();
    public IReadOnlyDictionary<int, double> Settings => _settings;

    /// <summary>Everything burned, as (x0,y0,x1,y1,power) — lets a test assert on the actual shape produced.</summary>
    public List<(double X0, double Y0, double X1, double Y1, double Power)> BurnedSegments { get; } = [];

    // ---------------------------------------------------------------- input

    public void Write(string data) => Write(Encoding.ASCII.GetBytes(data));

    public void Write(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (IsRealtimeByte(b))
            {
                HandleRealtime(b);
                continue;
            }

            _rxBytesPending++;
            if (_rxBytesPending > PeakRxBytes) PeakRxBytes = _rxBytesPending;
            if (_rxBytesPending > _options.RxBufferSize) BufferOverflowed = true;

            if (b is (byte)'\n' or (byte)'\r')
            {
                _pendingLines.Enqueue(_rxLine.ToString());
                _rxLine.Clear();
            }
            else
            {
                _rxLine.Append((char)b);
            }
        }

        DrainPendingLines();
    }

    /// <summary>
    /// Parse and acknowledge whatever fits.
    ///
    /// Real GRBL answers <c>ok</c> when a line has been parsed into the motion
    /// planner, not when the bytes arrive — so once the planner is full it simply
    /// stops answering, and the sender's in-flight byte count rises until the
    /// receive buffer is nearly full. That back-pressure is the entire mechanism
    /// character-counting streaming relies on, so the simulator has to reproduce it
    /// or the streaming tests prove nothing.
    /// </summary>
    private void DrainPendingLines()
    {
        while (_pendingLines.Count > 0)
        {
            var raw = _pendingLines.Peek();
            var line = raw.Trim();

            // Motion lines need a free planner block; everything else is parsed at once.
            if (LooksLikeMotion(line) && _planner.Count + (_active is null ? 0 : 1) >= _options.PlannerBlocks) return;

            _pendingLines.Dequeue();
            _rxBytesPending = Math.Max(0, _rxBytesPending - (raw.Length + 1));
            ProcessLine(line);
        }
    }

    /// <summary>Cheap test for whether a line will consume a planner block.</summary>
    private static bool LooksLikeMotion(string line)
    {
        if (line.Length == 0 || line[0] == '$') return false;
        var hasAxis = false;
        var hasMotionOrModal = false;
        foreach (var ch in line)
        {
            var c = char.ToUpperInvariant(ch);
            if (c is 'X' or 'Y' or 'Z') hasAxis = true;
            if (c is 'G' or 'X' or 'Y' or 'Z') hasMotionOrModal = true;
        }
        return hasAxis && hasMotionOrModal;
    }

    private static bool IsRealtimeByte(byte b) =>
        b is (byte)'?' or (byte)'~' or (byte)'!' or 0x18 || (b >= 0x84 && b <= 0xA1);

    private void HandleRealtime(byte b)
    {
        switch (b)
        {
            case (byte)'?':
                Emit(BuildStatusReport());
                break;

            case (byte)'!':
                if (_state == MachineMode.Run) _state = MachineMode.Hold;
                break;

            case (byte)'~':
                if (_state == MachineMode.Hold) _state = _planner.Count > 0 ? MachineMode.Run : MachineMode.Idle;
                break;

            case 0x18: // soft reset — discards the buffer and re-announces
                _planner.Clear();
                _pendingLines.Clear();
                _active = null;
                _activeElapsed = 0;
                _plannedX = _mx;
                _plannedY = _my;
                _plannedZ = _mz;
                _rxLine.Clear();
                _rxBytesPending = 0;
                _laserOn = false;
                _spindle = 0;
                _state = _options.HomingEnabled && _options.StartInAlarm ? MachineMode.Alarm : MachineMode.Idle;
                EmitWelcome();
                break;

            case 0x85: // jog cancel
                if (_state == MachineMode.Jog)
                {
                    _planner.Clear();
                    _active = null;
                    _plannedX = _mx;
                    _plannedY = _my;
                    _plannedZ = _mz;
                    _state = MachineMode.Idle;
                }
                break;
        }
    }

    private void ProcessLine(string line)
    {
        if (line.Length == 0)
        {
            Emit("ok");
            return;
        }

        LinesProcessed++;

        if (_options.FaultEveryNLines > 0 && LinesProcessed % _options.FaultEveryNLines == 0)
        {
            Emit("error:20");
            return;
        }

        if (line[0] == '$')
        {
            HandleSystemCommand(line);
            return;
        }

        if (_state == MachineMode.Alarm)
        {
            // Real GRBL locks out G-code until $X or $H.
            Emit("error:9");
            return;
        }

        var error = ExecuteGcode(line);
        Emit(error == 0 ? "ok" : $"error:{error}");
    }

    private void HandleSystemCommand(string line)
    {
        if (line == "$$")
        {
            foreach (var (key, value) in _settings.OrderBy(kv => kv.Key))
            {
                Emit($"${key}={value.ToString("0.###", CultureInfo.InvariantCulture)}");
            }
            Emit("ok");
            return;
        }

        if (line == "$")
        {
            Emit("[HLP:$$ $# $G $I $N $x=val $Nx=line $J=line $SLP $C $X $H ~ ! ? ctrl-x]");
            Emit("ok");
            return;
        }

        if (line == "$G")
        {
            var laser = _laserOn ? (_motion == 1 ? "M3" : "M4") : "M5";
            Emit($"[GC:G{_motion} G54 G17 G{(_unitScale > 1 ? 20 : 21)} G{(_absolute ? 90 : 91)} G94 {laser} M9 T0 " +
                 $"F{_feed.ToString("0.###", CultureInfo.InvariantCulture)} S{_spindle.ToString("0.###", CultureInfo.InvariantCulture)}]");
            Emit("ok");
            return;
        }

        if (line == "$I")
        {
            Emit($"[VER:{_options.FirmwareVersion}.20260101:Emberline Virtual]");
            Emit($"[OPT:VL,{_options.PlannerBlocks},{_options.RxBufferSize}]");
            Emit("ok");
            return;
        }

        if (line == "$#")
        {
            Emit($"[G54:{Fmt(_offsetX)},{Fmt(_offsetY)},{Fmt(_offsetZ)}]");
            Emit("[G55:0.000,0.000,0.000]");
            Emit("[G28:0.000,0.000,0.000]");
            Emit("[TLO:0.000]");
            Emit("[PRB:0.000,0.000,0.000:0]");
            Emit("ok");
            return;
        }

        if (line == "$X")
        {
            if (_state == MachineMode.Alarm)
            {
                _state = MachineMode.Idle;
                _alarmCode = 0;
                Emit("[MSG:Caution: Unlocked]");
            }
            Emit("ok");
            return;
        }

        if (line == "$H")
        {
            if (!_options.HomingEnabled)
            {
                Emit("error:5");
                return;
            }
            _state = MachineMode.Home;
            _homingRemaining = 2.0;
            Emit("ok");
            return;
        }

        if (line == "$C")
        {
            _state = _state == MachineMode.Check ? MachineMode.Idle : MachineMode.Check;
            Emit($"[MSG:{(_state == MachineMode.Check ? "Enabled" : "Disabled")}]");
            Emit("ok");
            return;
        }

        if (line.StartsWith("$J=", StringComparison.Ordinal))
        {
            if (_state is MachineMode.Alarm)
            {
                Emit("error:9");
                return;
            }
            var err = ExecuteGcode(line[3..], isJog: true);
            Emit(err == 0 ? "ok" : $"error:{err}");
            return;
        }

        // $<n>=<value>
        var eq = line.IndexOf('=');
        if (eq > 1 &&
            int.TryParse(line.AsSpan(1, eq - 1), out var settingKey) &&
            double.TryParse(line.AsSpan(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var settingValue))
        {
            if (_state is MachineMode.Run or MachineMode.Jog)
            {
                Emit("error:8");
                return;
            }
            _settings[settingKey] = settingValue;
            Emit("ok");
            return;
        }

        Emit("error:3");
    }

    private int ExecuteGcode(string line, bool isJog = false)
    {
        var targetX = _absolute ? _plannedX - _offsetX : 0;
        var targetY = _absolute ? _plannedY - _offsetY : 0;
        var targetZ = _absolute ? _plannedZ - _offsetZ : 0;
        var hasAxis = false;
        int? motionThisLine = null;
        var lineFeed = _feed;

        foreach (var (letter, value) in EnumerateWords(line))
        {
            switch (letter)
            {
                case 'G':
                {
                    var g = Math.Round(value, 1);
                    if (g is 0 or 1 or 2 or 3) motionThisLine = (int)g;
                    else if (g == 20) _unitScale = 25.4;
                    else if (g == 21) _unitScale = 1;
                    else if (g == 90) _absolute = true;
                    else if (g == 91) _absolute = false;
                    else if (g is 4 or 17 or 18 or 19 or 53 or 54 or 55 or 56 or 57 or 58 or 59 or 90.1 or 91.1 or 93 or 94) { }
                    else if (g == 92)
                    {
                        _offsetX = _plannedX;
                        _offsetY = _plannedY;
                        _offsetZ = _plannedZ;
                        return 0;
                    }
                    else return 20;
                    break;
                }
                case 'M':
                {
                    var m = (int)Math.Round(value);
                    if (m is 3 or 4) _laserOn = true;
                    else if (m == 5) { _laserOn = false; _spindle = 0; }
                    else if (m is 2 or 30) { _laserOn = false; }
                    else if (m is 7 or 8 or 9) { }
                    else return 20;
                    break;
                }
                case 'X': targetX = _absolute ? value * _unitScale : targetX + value * _unitScale; hasAxis = true; break;
                case 'Y': targetY = _absolute ? value * _unitScale : targetY + value * _unitScale; hasAxis = true; break;
                case 'Z': targetZ = _absolute ? value * _unitScale : targetZ + value * _unitScale; hasAxis = true; break;
                case 'F': lineFeed = value * _unitScale; _feed = lineFeed; break;
                case 'S': _spindle = value; break;
                case 'I': case 'J': case 'K': case 'R': case 'P': case 'N': case 'T': break;
                default: return 1;
            }
        }

        if (motionThisLine is { } m2) _motion = m2;
        if (!hasAxis) return 0;

        var absX = targetX + _offsetX;
        var absY = targetY + _offsetY;
        var absZ = targetZ + _offsetZ;

        if (_options.SoftLimitsEnabled &&
            (absX < -0.001 || absY < -0.001 || absX > _options.BedWidthMm + 0.001 || absY > _options.BedHeightMm + 0.001))
        {
            _state = MachineMode.Alarm;
            _alarmCode = 2;
            Emit("ALARM:2");
            return 0;
        }

        if (_motion != 0 && _motion != 1 && !isJog)
        {
            // Arcs are accepted and modelled as a straight move — the simulator
            // exercises the protocol, not the interpolator.
        }

        var isRapid = _motion == 0 && !isJog;
        var speed = isRapid ? Math.Max(1, _settings.GetValueOrDefault(110, 12000)) : Math.Max(1, lineFeed > 0 ? lineFeed : 600);
        var distance = Math.Sqrt((absX - _plannedX) * (absX - _plannedX) +
                                 (absY - _plannedY) * (absY - _plannedY) +
                                 (absZ - _plannedZ) * (absZ - _plannedZ));
        var duration = distance / (speed / 60.0);

        _planner.Enqueue(new PlannedMove(absX, absY, absZ, _plannedX, _plannedY, _plannedZ, duration,
                                         _laserOn ? _spindle : 0, isRapid));
        _plannedX = absX;
        _plannedY = absY;
        _plannedZ = absZ;

        if (_state == MachineMode.Idle) _state = isJog ? MachineMode.Jog : MachineMode.Run;

        return 0;
    }

    // -------------------------------------------------------------- ticking

    /// <summary>Advance the simulated clock. Nothing moves unless this is called.</summary>
    public void Tick(double seconds)
    {
        _elapsed += seconds;

        if (_state == MachineMode.Home)
        {
            _homingRemaining -= seconds;
            if (_homingRemaining <= 0)
            {
                _mx = _my = _mz = 0;
                _plannedX = _plannedY = _plannedZ = 0;
                _state = MachineMode.Idle;
            }
            return;
        }

        if (_state == MachineMode.Hold) return;

        var remaining = seconds;
        while (remaining > 0)
        {
            if (_active is null)
            {
                if (_planner.Count == 0) break;
                _active = _planner.Dequeue();
                _activeElapsed = 0;
            }

            var move = _active.Value;
            var left = move.DurationSeconds - _activeElapsed;

            if (left <= remaining || move.DurationSeconds <= 1e-9)
            {
                remaining -= Math.Max(0, left);
                AdvanceTo(move, move.TargetX, move.TargetY, move.TargetZ);
                if (move.Spindle > 0 && !move.Rapid)
                {
                    BurnedSegments.Add((move.StartX, move.StartY, move.TargetX, move.TargetY, move.Spindle));
                }
                _active = null;
                _activeElapsed = 0;
            }
            else
            {
                _activeElapsed += remaining;
                var t = Math.Clamp(_activeElapsed / move.DurationSeconds, 0, 1);
                AdvanceTo(move,
                    move.StartX + (move.TargetX - move.StartX) * t,
                    move.StartY + (move.TargetY - move.StartY) * t,
                    move.StartZ + (move.TargetZ - move.StartZ) * t);
                remaining = 0;
            }
        }

        if (_planner.Count == 0 && _active is null && _state is MachineMode.Run or MachineMode.Jog)
        {
            _state = MachineMode.Idle;
        }

        // Planner blocks have freed up, so more queued lines can now be accepted.
        DrainPendingLines();
    }

    /// <summary>Run until the machine is idle, in fixed steps. Used by tests that just want the job finished.</summary>
    public void RunToIdle(double stepSeconds = 0.05, int maxSteps = 2_000_000)
    {
        var steps = 0;
        while (!IsIdle && steps++ < maxSteps) Tick(stepSeconds);
    }

    /// <summary>Move the physical head, accumulating burn or travel distance as we go.</summary>
    private void AdvanceTo(PlannedMove move, double x, double y, double z)
    {
        var step = Math.Sqrt((x - _mx) * (x - _mx) + (y - _my) * (y - _my) + (z - _mz) * (z - _mz));

        if (move.Spindle > 0 && !move.Rapid) BurnDistanceMm += step;
        else TravelDistanceMm += step;

        _mx = x;
        _my = y;
        _mz = z;
    }

    // -------------------------------------------------------------- reports

    private string BuildStatusReport()
    {
        var stateText = _state switch
        {
            MachineMode.Hold => "Hold:0",
            MachineMode.Door => "Door:0",
            _ => _state.ToString(),
        };

        var plannerFree = Math.Max(0, _options.PlannerBlocks - _planner.Count - (_active is null ? 0 : 1));
        var rxFree = Math.Max(0, _options.RxBufferSize - _rxBytesPending);
        var currentFeed = _state == MachineMode.Run && _planner.Count > 0 ? _feed : 0;
        var currentSpindle = _laserOn ? _spindle : 0;

        return $"<{stateText}|MPos:{Fmt(_mx)},{Fmt(_my)},{Fmt(_mz)}|Bf:{plannerFree},{rxFree}|" +
               $"FS:{currentFeed.ToString("0", CultureInfo.InvariantCulture)},{currentSpindle.ToString("0", CultureInfo.InvariantCulture)}|" +
               $"WCO:{Fmt(_offsetX)},{Fmt(_offsetY)},{Fmt(_offsetZ)}|Ov:100,100,100>";
    }

    private void EmitWelcome() => Emit($"Grbl {_options.FirmwareVersion} ['$' for help]");

    private void Emit(string line)
    {
        _pendingOutput.Add(line);
        LineEmitted?.Invoke(line);
    }

    /// <summary>Drain everything emitted since the last call. For pull-style tests.</summary>
    public List<string> DrainOutput()
    {
        var copy = new List<string>(_pendingOutput);
        _pendingOutput.Clear();
        return copy;
    }

    private static string Fmt(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private static IEnumerable<(char Letter, double Value)> EnumerateWords(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '(')
            {
                while (i < line.Length && line[i] != ')') i++;
                continue;
            }
            if (ch == ';') yield break;
            if (!char.IsLetter(ch)) continue;

            var letter = char.ToUpperInvariant(ch);
            var start = i + 1;
            while (start < line.Length && char.IsWhiteSpace(line[start])) start++;

            var end = start;
            if (end < line.Length && (line[end] == '-' || line[end] == '+')) end++;
            var sawDigit = false;
            while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '.'))
            {
                if (char.IsDigit(line[end])) sawDigit = true;
                end++;
            }

            if (!sawDigit) continue;
            if (double.TryParse(line.AsSpan(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                yield return (letter, value);
            }
            i = end - 1;
        }
    }

    private Dictionary<int, double> DefaultSettings() => new()
    {
        [0] = 10, [1] = 25, [2] = 0, [3] = 0, [4] = 0, [5] = 0, [6] = 0,
        [10] = 3, [11] = 0.01, [12] = 0.002, [13] = 0,
        [20] = _options.SoftLimitsEnabled ? 1 : 0,
        [21] = 0,
        [22] = _options.HomingEnabled ? 1 : 0,
        [23] = 3, [24] = 100, [25] = 2000, [26] = 250, [27] = 2,
        [30] = 1000, [31] = 0, [32] = _options.LaserMode ? 1 : 0,
        [100] = 80, [101] = 80, [102] = 80,
        [110] = 12000, [111] = 12000, [112] = 600,
        [120] = 1500, [121] = 1500, [122] = 100,
        [130] = _options.BedWidthMm, [131] = _options.BedHeightMm, [132] = 50,
    };
}
