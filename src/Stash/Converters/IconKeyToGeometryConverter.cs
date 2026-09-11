using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Stash.Converters;

/// <summary>
/// Turns an icon resource key such as "Icon.Star" into the <see cref="Geometry"/>
/// registered under it.
/// </summary>
/// <remarks>
/// This is what lets the view models name an icon with a plain string and stay
/// free of any WPF drawing types.
/// </remarks>
public sealed class IconKeyToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0)
        {
            return null;
        }

        return Application.Current?.TryFindResource(key) as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
