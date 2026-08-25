using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Emberline.App.Converters;

/// <summary>
/// Resolves a token name — or a literal hex colour — to a brush from the current
/// theme. Lets a view model say "StateAlarm" without knowing what colour that is
/// in light versus dark, which is what keeps both themes honest.
/// </summary>
public sealed class ThemeBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0) return Brushes.Transparent;

        if (key[0] == '#')
        {
            try
            {
                return new SolidColorBrush(Color.Parse(key));
            }
            catch (FormatException)
            {
                return Brushes.Transparent;
            }
        }

        var app = Application.Current;
        if (app?.TryGetResource(key, app.ActualThemeVariant, out var resource) == true && resource is IBrush brush)
        {
            return brush;
        }

        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the bound value equals the parameter. For radio-style enum buttons.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null &&
        string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
