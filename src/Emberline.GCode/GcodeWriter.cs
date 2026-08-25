using System.Globalization;
using System.Text;

namespace Emberline.GCode;

/// <summary>
/// Emits G-code with modal suppression — a word is only written when its value
/// actually changed.
///
/// This is not tidiness. Every repeated <c>F3000</c> is six bytes of the
/// controller's 128-byte receive buffer, and on a dense raster that is the
/// difference between eight and five lines in flight. Suppression typically cuts
/// raster file size by a third and directly raises streaming throughput.
/// </summary>
public sealed class GcodeWriter
{
    private readonly List<string> _lines = [];
    private readonly StringBuilder _sb = new(48);
    private readonly int _decimals;

    private double _lastX = double.NaN;
    private double _lastY = double.NaN;
    private double _lastS = double.NaN;
    private double _lastF = double.NaN;
    private int _lastMotion = -1;

    public GcodeWriter(int decimals = 3) => _decimals = decimals;

    public IReadOnlyList<string> Lines => _lines;
    public int BurnMoveCount { get; private set; }
    public int TravelMoveCount { get; private set; }

    public double CurrentX => double.IsNaN(_lastX) ? 0 : _lastX;
    public double CurrentY => double.IsNaN(_lastY) ? 0 : _lastY;

    public void Raw(string line) => _lines.Add(line);

    public void Comment(string text) => _lines.Add("; " + text);

    public void Blank() => _lines.Add(string.Empty);

    /// <summary>
    /// Forget the modal state. Call after anything that could desync the
    /// controller's idea of the current words — a pass boundary, an M5, a raw block.
    /// </summary>
    public void ResetModal()
    {
        _lastS = double.NaN;
        _lastF = double.NaN;
        _lastMotion = -1;
    }

    /// <summary>Forget position as well. Only correct after an actual re-home or re-zero.</summary>
    public void ResetAll()
    {
        ResetModal();
        _lastX = double.NaN;
        _lastY = double.NaN;
    }

    public void Rapid(double x, double y, double? feed = null) => Move(0, x, y, null, feed);

    public void Linear(double x, double y, double? spindle = null, double? feed = null) => Move(1, x, y, spindle, feed);

    /// <summary>Set the S word without moving — for turning the beam off at the end of a run.</summary>
    public void SetSpindle(double s)
    {
        if (Same(s, _lastS)) return;
        _lastS = s;
        _lines.Add("S" + Num(s, 0));
    }

    public void SetFeed(double f)
    {
        if (Same(f, _lastF)) return;
        _lastF = f;
        _lines.Add("F" + Num(f, 0));
    }

    private void Move(int motion, double x, double y, double? spindle, double? feed)
    {
        var rx = Math.Round(x, _decimals);
        var ry = Math.Round(y, _decimals);

        var moved = !Same(rx, _lastX) || !Same(ry, _lastY);
        var spindleChanged = spindle.HasValue && !Same(spindle.Value, _lastS);
        var feedChanged = feed.HasValue && !Same(feed.Value, _lastF);

        if (!moved && !spindleChanged && !feedChanged) return;

        _sb.Clear();

        if (motion != _lastMotion || moved)
        {
            _sb.Append('G').Append(motion);
            _lastMotion = motion;
        }

        if (!Same(rx, _lastX))
        {
            _sb.Append(_sb.Length > 0 ? " X" : "X").Append(Num(rx, _decimals));
            _lastX = rx;
        }

        if (!Same(ry, _lastY))
        {
            _sb.Append(_sb.Length > 0 ? " Y" : "Y").Append(Num(ry, _decimals));
            _lastY = ry;
        }

        if (spindleChanged)
        {
            _sb.Append(_sb.Length > 0 ? " S" : "S").Append(Num(spindle!.Value, 0));
            _lastS = spindle.Value;
        }

        if (feedChanged)
        {
            _sb.Append(_sb.Length > 0 ? " F" : "F").Append(Num(feed!.Value, 0));
            _lastF = feed.Value;
        }

        if (_sb.Length == 0) return;

        _lines.Add(_sb.ToString());
        if (moved)
        {
            if (motion == 0 || (spindle ?? _lastS) <= 0) TravelMoveCount++;
            else BurnMoveCount++;
        }
    }

    private static bool Same(double a, double b) => !double.IsNaN(a) && !double.IsNaN(b) && Math.Abs(a - b) < 1e-9;

    private static string Num(double value, int decimals)
    {
        if (!double.IsFinite(value)) return "0";
        var s = value.ToString("F" + decimals, CultureInfo.InvariantCulture);
        if (decimals == 0) return s;
        s = s.TrimEnd('0').TrimEnd('.');
        return s.Length == 0 || s == "-" ? "0" : s;
    }

    public string Build() => string.Join('\n', _lines);
}
