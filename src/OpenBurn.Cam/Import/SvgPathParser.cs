using System.Globalization;
using OpenBurn.Core.Geometry;

namespace OpenBurn.Cam.Import;

/// <summary>
/// SVG path data parser.
///
/// Implements the full command set — M L H V C S Q T A Z in both absolute and
/// relative form — including the reflected control points of S and T, which are
/// the part everyone skips and which every real-world SVG from Illustrator or
/// Inkscape uses heavily.
/// </summary>
public static class SvgPathParser
{
    /// <summary>Parse path data into flattened polylines in the path's own user units.</summary>
    public static List<Polyline> Parse(string data, double tolerance = Curves.DefaultTolerance)
    {
        var result = new List<Polyline>();
        var tokens = new Tokenizer(data);

        Polyline? current = null;
        var cursor = Vec2.Zero;
        var subpathStart = Vec2.Zero;

        // Reflected control points for the S and T shorthand commands.
        var lastCubicControl = Vec2.Zero;
        var lastQuadControl = Vec2.Zero;
        var lastCommand = '\0';

        void StartSubpath(Vec2 at)
        {
            Flush();
            current = new Polyline();
            current.Add(at);
            subpathStart = at;
        }

        void Flush()
        {
            if (current is { Count: > 1 }) result.Add(current);
            current = null;
        }

        while (tokens.TryReadCommand(out var command))
        {
            var relative = char.IsLower(command);
            var op = char.ToUpperInvariant(command);

            // A command letter may be followed by several coordinate sets; repeats
            // of M implicitly become L, which is easy to miss and produces
            // spectacularly wrong geometry when it is.
            var firstIteration = true;

            do
            {
                switch (op)
                {
                    case 'M':
                    {
                        if (!tokens.TryReadPoint(out var p)) return Finish(result, ref current);
                        var target = relative ? cursor + p : p;
                        if (firstIteration) StartSubpath(target);
                        else current?.Add(target);
                        cursor = target;
                        break;
                    }

                    case 'L':
                    {
                        if (!tokens.TryReadPoint(out var p)) return Finish(result, ref current);
                        cursor = relative ? cursor + p : p;
                        (current ??= Seed(cursor)).Add(cursor);
                        break;
                    }

                    case 'H':
                    {
                        if (!tokens.TryReadNumber(out var x)) return Finish(result, ref current);
                        cursor = new Vec2(relative ? cursor.X + x : x, cursor.Y);
                        (current ??= Seed(cursor)).Add(cursor);
                        break;
                    }

                    case 'V':
                    {
                        if (!tokens.TryReadNumber(out var y)) return Finish(result, ref current);
                        cursor = new Vec2(cursor.X, relative ? cursor.Y + y : y);
                        (current ??= Seed(cursor)).Add(cursor);
                        break;
                    }

                    case 'C':
                    {
                        if (!tokens.TryReadPoint(out var c1) || !tokens.TryReadPoint(out var c2) || !tokens.TryReadPoint(out var end))
                        {
                            return Finish(result, ref current);
                        }
                        var a1 = relative ? cursor + c1 : c1;
                        var a2 = relative ? cursor + c2 : c2;
                        var e = relative ? cursor + end : end;
                        current ??= Seed(cursor);
                        Curves.FlattenCubic(current, cursor, a1, a2, e, tolerance);
                        lastCubicControl = a2;
                        cursor = e;
                        break;
                    }

                    case 'S':
                    {
                        if (!tokens.TryReadPoint(out var c2) || !tokens.TryReadPoint(out var end))
                        {
                            return Finish(result, ref current);
                        }
                        // First control point is the reflection of the previous one.
                        var a1 = lastCommand is 'C' or 'S' ? cursor * 2 - lastCubicControl : cursor;
                        var a2 = relative ? cursor + c2 : c2;
                        var e = relative ? cursor + end : end;
                        current ??= Seed(cursor);
                        Curves.FlattenCubic(current, cursor, a1, a2, e, tolerance);
                        lastCubicControl = a2;
                        cursor = e;
                        break;
                    }

                    case 'Q':
                    {
                        if (!tokens.TryReadPoint(out var c) || !tokens.TryReadPoint(out var end))
                        {
                            return Finish(result, ref current);
                        }
                        var a = relative ? cursor + c : c;
                        var e = relative ? cursor + end : end;
                        current ??= Seed(cursor);
                        Curves.FlattenQuadratic(current, cursor, a, e, tolerance);
                        lastQuadControl = a;
                        cursor = e;
                        break;
                    }

                    case 'T':
                    {
                        if (!tokens.TryReadPoint(out var end)) return Finish(result, ref current);
                        var a = lastCommand is 'Q' or 'T' ? cursor * 2 - lastQuadControl : cursor;
                        var e = relative ? cursor + end : end;
                        current ??= Seed(cursor);
                        Curves.FlattenQuadratic(current, cursor, a, e, tolerance);
                        lastQuadControl = a;
                        cursor = e;
                        break;
                    }

                    case 'A':
                    {
                        if (!tokens.TryReadNumber(out var rx) || !tokens.TryReadNumber(out var ry) ||
                            !tokens.TryReadNumber(out var rotation) || !tokens.TryReadFlag(out var largeArc) ||
                            !tokens.TryReadFlag(out var sweep) || !tokens.TryReadPoint(out var end))
                        {
                            return Finish(result, ref current);
                        }
                        var e = relative ? cursor + end : end;
                        current ??= Seed(cursor);
                        Curves.FlattenSvgArc(current, cursor, e, rx, ry, rotation, largeArc, sweep, tolerance);
                        cursor = e;
                        break;
                    }

                    case 'Z':
                    {
                        if (current is { Count: > 1 })
                        {
                            current.IsClosed = true;
                            result.Add(current);
                        }
                        current = null;
                        cursor = subpathStart;
                        break;
                    }

                    default:
                        // Unknown command: skip its operands rather than looping forever.
                        while (tokens.TryReadNumber(out _)) { }
                        break;
                }

                lastCommand = op;
                firstIteration = false;

                // After an M, subsequent coordinate pairs are implicit L commands.
                if (op == 'M') op = 'L';
            }
            while (op != 'Z' && tokens.PeekIsNumber);
        }

        Flush();
        return result;
    }

    private static Polyline Seed(Vec2 at)
    {
        var p = new Polyline();
        p.Add(at);
        return p;
    }

    private static List<Polyline> Finish(List<Polyline> result, ref Polyline? current)
    {
        if (current is { Count: > 1 }) result.Add(current);
        current = null;
        return result;
    }

    /// <summary>
    /// SVG path data is not whitespace-delimited in any reliable way: commas are
    /// optional, a minus sign starts a new number without a separator, and
    /// exponents are legal. This walks it character by character rather than
    /// pretending Split will do.
    /// </summary>
    private ref struct Tokenizer(ReadOnlySpan<char> data)
    {
        private readonly ReadOnlySpan<char> _data = data;
        private int _pos;

        private void SkipSeparators()
        {
            while (_pos < _data.Length && (char.IsWhiteSpace(_data[_pos]) || _data[_pos] == ',')) _pos++;
        }

        public bool PeekIsNumber
        {
            get
            {
                var save = _pos;
                SkipSeparatorsMutable(ref save);
                if (save >= _data.Length) return false;
                var c = _data[save];
                return char.IsDigit(c) || c == '-' || c == '+' || c == '.';
            }
        }

        private void SkipSeparatorsMutable(ref int pos)
        {
            while (pos < _data.Length && (char.IsWhiteSpace(_data[pos]) || _data[pos] == ',')) pos++;
        }

        public bool TryReadCommand(out char command)
        {
            SkipSeparators();
            command = '\0';
            if (_pos >= _data.Length) return false;

            var c = _data[_pos];
            if (!char.IsLetter(c)) return false;

            command = c;
            _pos++;
            return true;
        }

        public bool TryReadNumber(out double value)
        {
            SkipSeparators();
            value = 0;
            if (_pos >= _data.Length) return false;

            var start = _pos;
            if (_data[_pos] is '-' or '+') _pos++;

            var sawDigit = false;
            while (_pos < _data.Length && char.IsDigit(_data[_pos])) { _pos++; sawDigit = true; }

            if (_pos < _data.Length && _data[_pos] == '.')
            {
                _pos++;
                while (_pos < _data.Length && char.IsDigit(_data[_pos])) { _pos++; sawDigit = true; }
            }

            if (sawDigit && _pos < _data.Length && (_data[_pos] == 'e' || _data[_pos] == 'E'))
            {
                var expStart = _pos;
                _pos++;
                if (_pos < _data.Length && (_data[_pos] == '-' || _data[_pos] == '+')) _pos++;
                if (_pos < _data.Length && char.IsDigit(_data[_pos]))
                {
                    while (_pos < _data.Length && char.IsDigit(_data[_pos])) _pos++;
                }
                else
                {
                    _pos = expStart; // not actually an exponent
                }
            }

            if (!sawDigit)
            {
                _pos = start;
                return false;
            }

            return double.TryParse(_data[start.._pos], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Arc flags are single characters and may be written without separators, so
        /// "a1 1 0 011 1" is legal and means flags 0 and 1 followed by point (1,1).
        /// </summary>
        public bool TryReadFlag(out bool value)
        {
            SkipSeparators();
            value = false;
            if (_pos >= _data.Length) return false;

            var c = _data[_pos];
            if (c == '0') { _pos++; value = false; return true; }
            if (c == '1') { _pos++; value = true; return true; }

            // Tolerate a malformed file by falling back to a full number read.
            if (!TryReadNumber(out var n)) return false;
            value = n != 0;
            return true;
        }

        public bool TryReadPoint(out Vec2 point)
        {
            point = Vec2.Zero;
            if (!TryReadNumber(out var x) || !TryReadNumber(out var y)) return false;
            point = new Vec2(x, y);
            return true;
        }
    }
}
