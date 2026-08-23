namespace OpenBurn.Core.Units;

/// <summary>
/// Display units. OpenBurn stores everything in millimetres; this exists purely
/// so the UI can show inches to users who think in them. Nothing below the view
/// model should ever see an inch.
/// </summary>
public enum LengthUnit
{
    Millimetres,
    Inches,
}

public static class UnitConvert
{
    public const double MmPerInch = 25.4;

    public static double ToMm(double value, LengthUnit from) =>
        from == LengthUnit.Inches ? value * MmPerInch : value;

    public static double FromMm(double mm, LengthUnit to) =>
        to == LengthUnit.Inches ? mm / MmPerInch : mm;

    public static string Suffix(this LengthUnit unit) => unit == LengthUnit.Inches ? "in" : "mm";

    /// <summary>Sensible decimal places for display — inches need more.</summary>
    public static int Precision(this LengthUnit unit) => unit == LengthUnit.Inches ? 4 : 2;

    public static string Format(double mm, LengthUnit unit) =>
        $"{FromMm(mm, unit).ToString("0." + new string('#', unit.Precision()), System.Globalization.CultureInfo.InvariantCulture)} {unit.Suffix()}";

    /// <summary>Speed is mm/min internally; inch users expect inches per minute.</summary>
    public static double SpeedToDisplay(double mmPerMin, LengthUnit unit) =>
        unit == LengthUnit.Inches ? mmPerMin / MmPerInch : mmPerMin;

    public static double SpeedFromDisplay(double value, LengthUnit unit) =>
        unit == LengthUnit.Inches ? value * MmPerInch : value;

    /// <summary>Lines-per-mm ⇄ DPI, the two ways people express raster resolution.</summary>
    public static double IntervalToDpi(double intervalMm) => MmPerInch / Math.Max(intervalMm, 0.001);
    public static double DpiToInterval(double dpi) => MmPerInch / Math.Max(dpi, 1);
}
