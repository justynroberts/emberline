using OpenBurn.Core.Geometry;

namespace OpenBurn.Core.Documents;

public enum TextAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>
/// Live text. The glyph outlines are produced by whatever font engine the host
/// has (Skia in the desktop app) and cached here; Core stays free of font
/// dependencies, and the text remains editable rather than being converted to
/// dead paths on creation.
/// </summary>
public sealed class TextShape : Shape
{
    private string _text = "Text";
    private string _fontFamily = "Bricolage Grotesque";
    private double _fontSizeMm = 10;
    private bool _bold;
    private bool _italic;
    private double _letterSpacingMm;
    private double _lineHeightFactor = 1.2;
    private TextAlignment _alignment = TextAlignment.Left;
    private List<Polyline> _outlines = [];

    public string Text
    {
        get => _text;
        set { Set(ref _text, value); OutlinesStale = true; }
    }

    public string FontFamily
    {
        get => _fontFamily;
        set { Set(ref _fontFamily, value); OutlinesStale = true; }
    }

    public double FontSizeMm
    {
        get => _fontSizeMm;
        set { Set(ref _fontSizeMm, Math.Max(0.5, value)); OutlinesStale = true; }
    }

    public bool Bold { get => _bold; set { Set(ref _bold, value); OutlinesStale = true; } }
    public bool Italic { get => _italic; set { Set(ref _italic, value); OutlinesStale = true; } }

    public double LetterSpacingMm
    {
        get => _letterSpacingMm;
        set { Set(ref _letterSpacingMm, value); OutlinesStale = true; }
    }

    public double LineHeightFactor
    {
        get => _lineHeightFactor;
        set { Set(ref _lineHeightFactor, Math.Max(0.5, value)); OutlinesStale = true; }
    }

    public TextAlignment Alignment
    {
        get => _alignment;
        set { Set(ref _alignment, value); OutlinesStale = true; }
    }

    /// <summary>Set when a property changed and the host needs to re-run the font engine.</summary>
    public bool OutlinesStale { get; private set; } = true;

    /// <summary>Called by the host's text service once it has traced the glyphs.</summary>
    public void SetOutlines(IEnumerable<Polyline> outlines)
    {
        _outlines = [.. outlines];
        OutlinesStale = false;
        OnChanged(nameof(Outlines));
    }

    public IReadOnlyList<Polyline> Outlines => _outlines;

    public override Rect2 LocalBounds
    {
        get
        {
            var r = Rect2.Empty;
            foreach (var p in _outlines) r = r.Union(p.Bounds);
            return r;
        }
    }

    public override IReadOnlyList<Polyline> GetOutlines(double tolerance = Curves.DefaultTolerance)
    {
        var t = Transform;
        var result = new List<Polyline>(_outlines.Count);
        foreach (var p in _outlines) result.Add(p.Transformed(t));
        return result;
    }

    /// <summary>Convert to dead paths — after this the text is no longer editable.</summary>
    public PathShape ToPathShape()
    {
        var s = new PathShape(_outlines.Select(p => p.Clone())) { Name = _text };
        CopyBaseTo(s);
        return s;
    }

    public override Shape Clone()
    {
        var copy = new TextShape
        {
            Text = _text,
            FontFamily = _fontFamily,
            FontSizeMm = _fontSizeMm,
            Bold = _bold,
            Italic = _italic,
            LetterSpacingMm = _letterSpacingMm,
            LineHeightFactor = _lineHeightFactor,
            Alignment = _alignment,
        };
        copy.SetOutlines(_outlines.Select(p => p.Clone()));
        CopyBaseTo(copy);
        return copy;
    }
}
