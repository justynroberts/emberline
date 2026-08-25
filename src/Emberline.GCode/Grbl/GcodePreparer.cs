using System.Text;

namespace Emberline.GCode.Grbl;

/// <summary>Turns a raw G-code file into lines that are safe and efficient to stream.</summary>
public static class GcodePreparer
{
    /// <summary>
    /// Strip comments and whitespace, drop blanks, and uppercase.
    ///
    /// Whitespace removal is not cosmetic: every space occupies a byte of the
    /// controller's 128-byte receive buffer, so stripping them measurably raises
    /// the number of lines that fit in flight.
    /// </summary>
    public static List<string> Prepare(string text, bool uppercase = true)
    {
        var result = new List<string>();
        var sb = new StringBuilder(96);

        foreach (var rawLine in text.Split('\n'))
        {
            sb.Clear();
            var inParens = false;

            foreach (var ch in rawLine)
            {
                if (ch == '(') { inParens = true; continue; }
                if (ch == ')') { inParens = false; continue; }
                if (inParens) continue;
                if (ch == ';') break;
                if (char.IsWhiteSpace(ch)) continue;
                sb.Append(uppercase ? char.ToUpperInvariant(ch) : ch);
            }

            if (sb.Length > 0) result.Add(sb.ToString());
        }

        return result;
    }

    /// <summary>
    /// Build the block that safely re-enters a job partway through. Resuming without
    /// re-stating the modal groups is how people put a cut at the wrong feed rate
    /// through a finished piece.
    /// </summary>
    public static List<string> BuildResumePreamble(IReadOnlyList<string> lines, int resumeAtIndex)
    {
        var units = "G21";
        var distance = "G90";
        var plane = "G17";
        string? feed = null;
        string? spindle = null;
        var laser = "M5";

        for (var i = 0; i < Math.Min(resumeAtIndex, lines.Count); i++)
        {
            var line = lines[i];
            for (var c = 0; c < line.Length; c++)
            {
                var letter = line[c];
                if (letter is not ('G' or 'M' or 'F' or 'S')) continue;

                var start = c + 1;
                var end = start;
                while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '.')) end++;
                if (end == start) continue;
                var value = line[start..end];

                switch (letter)
                {
                    case 'G' when value is "20" or "21": units = "G" + value; break;
                    case 'G' when value is "90" or "91": distance = "G" + value; break;
                    case 'G' when value is "17" or "18" or "19": plane = "G" + value; break;
                    case 'M' when value is "3" or "03" or "4" or "04": laser = "M" + value.TrimStart('0'); break;
                    case 'M' when value is "5" or "05": laser = "M5"; break;
                    case 'F': feed = "F" + value; break;
                    case 'S': spindle = "S" + value; break;
                }
                c = end - 1;
            }
        }

        var preamble = new List<string> { units, distance, plane };
        if (spindle is not null) preamble.Add(spindle);
        if (feed is not null) preamble.Add(feed);
        preamble.Add(laser);
        return preamble;
    }
}
