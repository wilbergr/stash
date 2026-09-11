using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Stash.Converters;

/// <summary>
/// Collapses an element when a boolean is true — the mirror of
/// <see cref="System.Windows.Controls.BooleanToVisibilityConverter"/>.
/// </summary>
/// <remarks>
/// Used so the footer's keyboard legend gives way to a toast rather than the two
/// overlapping, which they otherwise do in the narrow left/right docks.
/// </remarks>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}
