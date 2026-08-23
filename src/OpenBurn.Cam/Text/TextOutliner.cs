using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;
using SkiaSharp;

namespace OpenBurn.Cam.Text;

public sealed record TextLayoutOptions
{
    /// <summary>Em size in millimetres.</summary>
    public double FontSizeMm { get; init; } = 10;

    public string FontFamily { get; init; } = "Bricolage Grotesque";
    public bool Bold { get; init; }
    public bool Italic { get; init; }

    /// <summary>Extra space between glyphs, millimetres. Negative tightens.</summary>
    public double LetterSpacingMm { get; init; }

    /// <summary>Line pitch as a multiple of the em size.</summary>
    public double LineHeightFactor { get; init; } = 1.2;

    public TextAlignment Alignment { get; init; } = TextAlignment.Left;

    /// <summary>Chord tolerance for flattening glyph curves, millimetres.</summary>
    public double ToleranceMm { get; init; } = 0.02;

    public static readonly TextLayoutOptions Default = new();
}

public sealed record TextOutlineResult(
    IReadOnlyList<Polyline> Outlines,
    double WidthMm,
    double HeightMm,
    string ResolvedFamily)
{
    /// <summary>True when the requested family was not installed and a substitute was used.</summary>
    public bool FontWasSubstituted { get; init; }
}

/// <summary>
/// Turns text into cuttable outlines.
///
/// A laser has no concept of a font: it follows a path. So text has to become
/// glyph contours before anything else can happen to it, and the two details that
/// decide whether the result is usable are here — counters must stay as separate
/// closed contours (or the inside of every 'o' burns solid), and the baseline has
/// to be flipped, because a font's Y grows downward and a bed's grows up.
/// </summary>
public static class TextOutliner
{
    /// <summary>
    /// Skia works in font units; asking for a large em and scaling down afterwards
    /// keeps the curve flattening well away from integer rounding in the hinter.
    /// </summary>
    private const float WorkingEm = 512f;

    public static TextOutlineResult Create(string text, TextLayoutOptions? options = null)
    {
        var o = options ?? TextLayoutOptions.Default;
        var outlines = new List<Polyline>();

        if (string.IsNullOrEmpty(text)) return new TextOutlineResult(outlines, 0, 0, o.FontFamily);

        var (typeface, substituted) = ResolveTypeface(o);
        using var font = new SKFont(typeface, WorkingEm);

        // Font units to millimetres.
        var scale = o.FontSizeMm / WorkingEm;
        var letterSpacingUnits = (float)(o.LetterSpacingMm / scale);
        var lineHeightUnits = (float)(WorkingEm * o.LineHeightFactor);

        var lines = text.Replace("\r\n", "\n").Split('\n');

        // Measure first so alignment and the overall bounds are known before any
        // geometry is produced.
        var widths = new float[lines.Length];
        var widest = 0f;
        for (var i = 0; i < lines.Length; i++)
        {
            widths[i] = MeasureLine(font, lines[i], letterSpacingUnits);
            if (widths[i] > widest) widest = widths[i];
        }

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.Length == 0) continue;

            var startX = o.Alignment switch
            {
                TextAlignment.Center => (widest - widths[lineIndex]) / 2,
                TextAlignment.Right => widest - widths[lineIndex],
                _ => 0f,
            };

            // Baselines march downward in font space; the flip to machine
            // orientation happens once, at the end.
            var baselineY = lineIndex * lineHeightUnits;

            AppendLine(outlines, font, line, startX, baselineY, letterSpacingUnits, scale, o.ToleranceMm);
        }

        // Flip Y and drop the result onto the origin, so the caller gets a shape
        // whose bottom-left corner is (0, 0) like every other shape in the document.
        var flipped = new List<Polyline>(outlines.Count);
        foreach (var p in outlines) flipped.Add(p.Transformed(Matrix2D.Scale(1, -1)));

        var bounds = Rect2.Empty;
        foreach (var p in flipped) bounds = bounds.Union(p.Bounds);

        var result = new List<Polyline>(flipped.Count);
        if (!bounds.IsEmpty)
        {
            var toOrigin = Matrix2D.Translate(-bounds.MinX, -bounds.MinY);
            foreach (var p in flipped) result.Add(p.Transformed(toOrigin));
        }
        else
        {
            result.AddRange(flipped);
        }

        return new TextOutlineResult(
            result,
            bounds.IsEmpty ? 0 : bounds.Width,
            bounds.IsEmpty ? 0 : bounds.Height,
            typeface.FamilyName)
        {
            FontWasSubstituted = substituted,
        };
    }

    private static float MeasureLine(SKFont font, string line, float letterSpacingUnits)
    {
        if (line.Length == 0) return 0;
        var width = font.MeasureText(line);
        return width + letterSpacingUnits * (line.Length - 1);
    }

    private static void AppendLine(
        List<Polyline> outlines,
        SKFont font,
        string line,
        float startX,
        float baselineY,
        float letterSpacingUnits,
        double scale,
        double toleranceMm)
    {
        // Letter spacing means glyphs have to be placed individually rather than
        // handing Skia the whole string.
        var cursor = startX;
        var toleranceUnits = toleranceMm / scale;

        foreach (var character in line)
        {
            var glyph = character.ToString();
            var advance = font.MeasureText(glyph);

            if (!char.IsWhiteSpace(character))
            {
                using var path = font.GetTextPath(glyph, new SKPoint(cursor, baselineY));
                if (path is not null) Flatten(path, outlines, scale, toleranceUnits);
            }

            cursor += advance + letterSpacingUnits;
        }
    }

    /// <summary>
    /// Walk a glyph path and emit one closed polyline per contour.
    ///
    /// Per contour matters: a glyph with a counter — o, a, 8, B — is several
    /// contours, and merging them into one path fills the holes.
    /// </summary>
    private static void Flatten(SKPath path, List<Polyline> outlines, double scale, double toleranceUnits)
    {
        using var iterator = path.CreateRawIterator();
        var points = new SKPoint[4];

        Polyline? current = null;
        var start = new Vec2();

        void Emit()
        {
            if (current is { Count: > 2 })
            {
                current.IsClosed = true;
                outlines.Add(current);
            }
            current = null;
        }

        Vec2 P(SKPoint p) => new(p.X * scale, p.Y * scale);

        SKPathVerb verb;
        while ((verb = iterator.Next(points)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    Emit();
                    current = new Polyline();
                    start = P(points[0]);
                    current.Add(start);
                    break;

                case SKPathVerb.Line:
                    current ??= Seed(P(points[0]));
                    current.Add(P(points[1]));
                    break;

                case SKPathVerb.Quad:
                    current ??= Seed(P(points[0]));
                    Curves.FlattenQuadratic(current, P(points[0]), P(points[1]), P(points[2]), toleranceUnits * scale);
                    break;

                case SKPathVerb.Conic:
                    // Rational quadratics appear in a few CFF fonts. Approximating
                    // one as a plain quadratic is at most a fraction of a chord
                    // tolerance out at laser resolution.
                    current ??= Seed(P(points[0]));
                    Curves.FlattenQuadratic(current, P(points[0]), P(points[1]), P(points[2]), toleranceUnits * scale);
                    break;

                case SKPathVerb.Cubic:
                    current ??= Seed(P(points[0]));
                    Curves.FlattenCubic(current, P(points[0]), P(points[1]), P(points[2]), P(points[3]), toleranceUnits * scale);
                    break;

                case SKPathVerb.Close:
                    if (current is { Count: > 1 }) current.Add(start);
                    Emit();
                    break;
            }
        }

        Emit();

        static Polyline Seed(Vec2 at)
        {
            var p = new Polyline();
            p.Add(at);
            return p;
        }
    }

    private static (SKTypeface Typeface, bool Substituted) ResolveTypeface(TextLayoutOptions o)
    {
        var style = new SKFontStyle(
            o.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            o.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

        var matched = SKFontManager.Default.MatchFamily(o.FontFamily, style);
        if (matched is not null)
        {
            // MatchFamily falls back to a default rather than returning null, so
            // compare the name to find out whether the request was actually honoured.
            var substituted = !string.Equals(matched.FamilyName, o.FontFamily, StringComparison.OrdinalIgnoreCase);
            return (matched, substituted);
        }

        return (SKTypeface.Default, true);
    }

    /// <summary>Every font family installed on this machine, for the font picker.</summary>
    public static IReadOnlyList<string> AvailableFamilies()
    {
        try
        {
            return [.. SKFontManager.Default.GetFontFamilies().Distinct().Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception)
        {
            return ["Sans Serif"];
        }
    }

    /// <summary>Fill a <see cref="TextShape"/> with outlines from its own properties.</summary>
    public static TextOutlineResult Apply(TextShape shape, double toleranceMm = 0.02)
    {
        var result = Create(shape.Text, new TextLayoutOptions
        {
            FontSizeMm = shape.FontSizeMm,
            FontFamily = shape.FontFamily,
            Bold = shape.Bold,
            Italic = shape.Italic,
            LetterSpacingMm = shape.LetterSpacingMm,
            LineHeightFactor = shape.LineHeightFactor,
            Alignment = shape.Alignment,
            ToleranceMm = toleranceMm,
        });

        shape.SetOutlines(result.Outlines);
        return result;
    }
}
