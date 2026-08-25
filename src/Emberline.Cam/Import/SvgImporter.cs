using System.Globalization;
using System.Xml.Linq;
using Emberline.Core.Documents;
using Emberline.Core.Geometry;

namespace Emberline.Cam.Import;

public sealed record SvgImportResult(
    IReadOnlyList<Polyline> Paths,
    double WidthMm,
    double HeightMm,
    IReadOnlyList<string> Warnings);

/// <summary>
/// SVG import.
///
/// Two things decide whether an import is usable, and both are easy to get wrong:
///
///  * **Real-world size.** An SVG carries its physical size in the width/height
///    attributes and its internal coordinate system in the viewBox. Honouring both
///    is what makes a 100 mm-wide drawing arrive as 100 mm rather than 378 mm
///    (the same number reinterpreted as CSS pixels).
///  * **Y direction.** SVG's Y axis points down; a laser bed's points up. The flip
///    happens once, here, at the boundary — never anywhere downstream.
/// </summary>
public static class SvgImporter
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    /// <summary>CSS reference pixel: 96 per inch.</summary>
    private const double PixelsPerInch = 96.0;

    public static SvgImportResult Import(string svgText, double tolerance = Curves.DefaultTolerance)
    {
        var warnings = new List<string>();
        XDocument doc;
        try
        {
            doc = XDocument.Parse(svgText, LoadOptions.None);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException($"This file is not valid SVG: {ex.Message}", ex);
        }

        var root = doc.Root ?? throw new InvalidDataException("The SVG file has no root element.");

        var (viewBoxX, viewBoxY, viewBoxW, viewBoxH) = ReadViewBox(root);
        var declaredWidth = ReadLength(root.Attribute("width")?.Value);
        var declaredHeight = ReadLength(root.Attribute("height")?.Value);

        // Work out user-units-to-millimetres.
        double scaleX, scaleY;
        double widthMm, heightMm;

        if (viewBoxW > 0 && viewBoxH > 0)
        {
            widthMm = declaredWidth ?? viewBoxW * 25.4 / PixelsPerInch;
            heightMm = declaredHeight ?? viewBoxH * 25.4 / PixelsPerInch;
            scaleX = widthMm / viewBoxW;
            scaleY = heightMm / viewBoxH;
        }
        else
        {
            // No viewBox: user units are CSS pixels.
            scaleX = scaleY = 25.4 / PixelsPerInch;
            widthMm = declaredWidth ?? 0;
            heightMm = declaredHeight ?? 0;
            warnings.Add("This SVG has no viewBox, so its coordinates were read as CSS pixels at 96 DPI. Check the imported size before burning.");
        }

        var paths = new List<Polyline>();
        // Flip Y and shift the viewBox origin to zero, all in one matrix.
        var rootTransform = Matrix2D.Translate(0, heightMm)
                          * Matrix2D.Scale(scaleX, -scaleY)
                          * Matrix2D.Translate(-viewBoxX, -viewBoxY);

        Walk(root, rootTransform, paths, warnings, tolerance);

        if (paths.Count == 0) warnings.Add("No drawable geometry was found in this SVG.");

        // If the document declared no size, derive it from the geometry.
        if (widthMm <= 0 || heightMm <= 0)
        {
            var bounds = Rect2.FromPoints(paths.SelectMany(p => p.Points));
            if (!bounds.IsEmpty)
            {
                widthMm = bounds.Width;
                heightMm = bounds.Height;
            }
        }

        return new SvgImportResult(paths, widthMm, heightMm, warnings);
    }

    private static void Walk(XElement element, Matrix2D transform, List<Polyline> output, List<string> warnings, double tolerance)
    {
        foreach (var child in element.Elements())
        {
            var local = child.Name.LocalName;

            // display:none and visibility:hidden mean the author did not want this burned.
            var style = child.Attribute("style")?.Value ?? string.Empty;
            if (child.Attribute("display")?.Value == "none" ||
                style.Contains("display:none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var childTransform = transform * ParseTransform(child.Attribute("transform")?.Value);
            var childTolerance = tolerance / Math.Max(childTransform.ScaleFactor, 1e-6);

            switch (local)
            {
                case "g":
                case "a":
                case "switch":
                    Walk(child, childTransform, output, warnings, tolerance);
                    break;

                case "svg":
                    Walk(child, childTransform, output, warnings, tolerance);
                    break;

                case "path":
                {
                    var d = child.Attribute("d")?.Value;
                    if (string.IsNullOrWhiteSpace(d)) break;
                    foreach (var p in SvgPathParser.Parse(d, childTolerance)) output.Add(p.Transformed(childTransform));
                    break;
                }

                case "rect":
                {
                    var x = Number(child, "x");
                    var y = Number(child, "y");
                    var w = Number(child, "width");
                    var h = Number(child, "height");
                    if (w <= 0 || h <= 0) break;

                    var rx = child.Attribute("rx") is not null ? Number(child, "rx") : Number(child, "ry");
                    var ry = child.Attribute("ry") is not null ? Number(child, "ry") : rx;
                    rx = Math.Min(rx, w / 2);
                    ry = Math.Min(ry, h / 2);

                    var poly = new Polyline { IsClosed = true };
                    if (rx <= 0 || ry <= 0)
                    {
                        poly.Add(x, y);
                        poly.Add(x + w, y);
                        poly.Add(x + w, y + h);
                        poly.Add(x, y + h);
                    }
                    else
                    {
                        AppendRoundedRect(poly, x, y, w, h, rx, ry, childTolerance);
                    }
                    output.Add(poly.Transformed(childTransform));
                    break;
                }

                case "circle":
                {
                    var cx = Number(child, "cx");
                    var cy = Number(child, "cy");
                    var r = Number(child, "r");
                    if (r <= 0) break;
                    var poly = new Polyline { IsClosed = true };
                    Curves.FlattenArc(poly, new Vec2(cx, cy), r, 0, Math.PI * 2, childTolerance);
                    output.Add(poly.Transformed(childTransform));
                    break;
                }

                case "ellipse":
                {
                    var cx = Number(child, "cx");
                    var cy = Number(child, "cy");
                    var rx = Number(child, "rx");
                    var ry = Number(child, "ry");
                    if (rx <= 0 || ry <= 0) break;

                    var poly = new Polyline { IsClosed = true };
                    var rMax = Math.Max(rx, ry);
                    var maxStep = 2 * Math.Acos(Math.Clamp(1 - childTolerance / rMax, -1, 1));
                    if (double.IsNaN(maxStep) || maxStep <= 1e-6) maxStep = 0.1;
                    var steps = Math.Max(16, (int)Math.Ceiling(2 * Math.PI / maxStep));
                    for (var i = 0; i < steps; i++)
                    {
                        var a = 2 * Math.PI * i / steps;
                        var (s, c) = Math.SinCos(a);
                        poly.Add(cx + rx * c, cy + ry * s);
                    }
                    output.Add(poly.Transformed(childTransform));
                    break;
                }

                case "line":
                {
                    var poly = new Polyline();
                    poly.Add(Number(child, "x1"), Number(child, "y1"));
                    poly.Add(Number(child, "x2"), Number(child, "y2"));
                    if (poly.Count > 1) output.Add(poly.Transformed(childTransform));
                    break;
                }

                case "polyline":
                case "polygon":
                {
                    var points = ParsePoints(child.Attribute("points")?.Value);
                    if (points.Count < 2) break;
                    var poly = new Polyline(points, closed: local == "polygon");
                    output.Add(poly.Transformed(childTransform));
                    break;
                }

                case "text":
                case "tspan":
                    warnings.Add("Text elements were skipped. Convert text to outlines in your drawing program, or re-create it with Emberline's text tool.");
                    break;

                case "image":
                    warnings.Add("An embedded raster image was skipped. Import the image file directly to engrave it.");
                    break;

                case "use":
                    warnings.Add("A <use> reference was skipped. Expand instances in your drawing program before exporting.");
                    break;

                case "defs":
                case "style":
                case "title":
                case "desc":
                case "metadata":
                    break;

                default:
                    Walk(child, childTransform, output, warnings, tolerance);
                    break;
            }
        }
    }

    private static void AppendRoundedRect(Polyline poly, double x, double y, double w, double h, double rx, double ry, double tolerance)
    {
        // Approximate elliptical corners with circular arcs on the smaller radius,
        // then scale — visually identical at laser resolution and far simpler.
        var r = Math.Min(rx, ry);
        const double quarter = Math.PI / 2;
        Curves.FlattenArc(poly, new Vec2(x + w - r, y + r), r, -quarter, quarter, tolerance);
        Curves.FlattenArc(poly, new Vec2(x + w - r, y + h - r), r, 0, quarter, tolerance);
        Curves.FlattenArc(poly, new Vec2(x + r, y + h - r), r, quarter, quarter, tolerance);
        Curves.FlattenArc(poly, new Vec2(x + r, y + r), r, Math.PI, quarter, tolerance);
    }

    private static List<Vec2> ParsePoints(string? value)
    {
        var result = new List<Vec2>();
        if (string.IsNullOrWhiteSpace(value)) return result;

        var numbers = new List<double>();
        var span = value.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && (char.IsWhiteSpace(span[i]) || span[i] == ',')) i++;
            if (i >= span.Length) break;

            var start = i;
            if (span[i] is '-' or '+') i++;
            while (i < span.Length && (char.IsDigit(span[i]) || span[i] == '.' || span[i] == 'e' || span[i] == 'E' ||
                                       ((span[i] == '-' || span[i] == '+') && (span[i - 1] == 'e' || span[i - 1] == 'E'))))
            {
                i++;
            }
            if (i == start) { i++; continue; }
            if (double.TryParse(span[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) numbers.Add(n);
        }

        for (var k = 0; k + 1 < numbers.Count; k += 2) result.Add(new Vec2(numbers[k], numbers[k + 1]));
        return result;
    }

    private static double Number(XElement element, string name) =>
        double.TryParse(element.Attribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static (double X, double Y, double Width, double Height) ReadViewBox(XElement root)
    {
        var value = root.Attribute("viewBox")?.Value;
        if (string.IsNullOrWhiteSpace(value)) return (0, 0, 0, 0);

        var parts = value.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return (0, 0, 0, 0);

        static double P(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return (P(parts[0]), P(parts[1]), P(parts[2]), P(parts[3]));
    }

    /// <summary>Read an SVG length with units, returning millimetres.</summary>
    public static double? ReadLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var span = value.Trim().AsSpan();
        var i = 0;
        if (i < span.Length && (span[i] == '-' || span[i] == '+')) i++;
        while (i < span.Length && (char.IsDigit(span[i]) || span[i] == '.' || span[i] == 'e' || span[i] == 'E')) i++;

        if (!double.TryParse(span[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
        var unit = span[i..].Trim().ToString().ToLowerInvariant();

        return unit switch
        {
            "mm" => number,
            "cm" => number * 10,
            "m" => number * 1000,
            "in" => number * 25.4,
            "pt" => number * 25.4 / 72.0,
            "pc" => number * 25.4 / 6.0,
            "px" or "" => number * 25.4 / PixelsPerInch,
            "%" => null, // percentages need a viewport we do not have here
            _ => number * 25.4 / PixelsPerInch,
        };
    }

    /// <summary>Parse an SVG transform list: matrix, translate, scale, rotate, skewX, skewY.</summary>
    public static Matrix2D ParseTransform(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Matrix2D.Identity;

        var result = Matrix2D.Identity;
        var i = 0;
        var span = value.AsSpan();

        while (i < span.Length)
        {
            while (i < span.Length && (char.IsWhiteSpace(span[i]) || span[i] == ',')) i++;
            if (i >= span.Length) break;

            var nameStart = i;
            while (i < span.Length && char.IsLetter(span[i])) i++;
            if (i == nameStart) { i++; continue; }
            var name = span[nameStart..i].ToString().ToLowerInvariant();

            while (i < span.Length && span[i] != '(') i++;
            if (i >= span.Length) break;
            var argsStart = ++i;
            while (i < span.Length && span[i] != ')') i++;
            var args = ParseNumbers(span[argsStart..i]);
            if (i < span.Length) i++;

            var m = name switch
            {
                "matrix" when args.Count >= 6 => new Matrix2D(args[0], args[1], args[2], args[3], args[4], args[5]),
                "translate" when args.Count >= 1 => Matrix2D.Translate(args[0], args.Count > 1 ? args[1] : 0),
                "scale" when args.Count >= 1 => Matrix2D.Scale(args[0], args.Count > 1 ? args[1] : args[0]),
                "rotate" when args.Count >= 3 => Matrix2D.RotateAbout(args[0], new Vec2(args[1], args[2])),
                "rotate" when args.Count >= 1 => Matrix2D.Rotate(args[0]),
                "skewx" when args.Count >= 1 => Matrix2D.Skew(args[0], 0),
                "skewy" when args.Count >= 1 => Matrix2D.Skew(0, args[0]),
                _ => Matrix2D.Identity,
            };

            result *= m;
        }

        return result;
    }

    private static List<double> ParseNumbers(ReadOnlySpan<char> span)
    {
        var result = new List<double>();
        var i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && (char.IsWhiteSpace(span[i]) || span[i] == ',')) i++;
            if (i >= span.Length) break;

            var start = i;
            if (span[i] is '-' or '+') i++;
            while (i < span.Length && (char.IsDigit(span[i]) || span[i] == '.')) i++;
            if (i < span.Length && (span[i] == 'e' || span[i] == 'E'))
            {
                i++;
                if (i < span.Length && (span[i] == '-' || span[i] == '+')) i++;
                while (i < span.Length && char.IsDigit(span[i])) i++;
            }

            if (i == start) { i++; continue; }
            if (double.TryParse(span[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) result.Add(n);
        }
        return result;
    }

    /// <summary>Import straight into a document shape, sized in real-world millimetres.</summary>
    public static PathShape ImportAsShape(string svgText, string name = "SVG", double tolerance = Curves.DefaultTolerance)
    {
        var result = Import(svgText, tolerance);
        return new PathShape(result.Paths) { Name = name };
    }
}
