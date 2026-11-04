using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Theater3D.App.Controls;

/// <summary>真偽を反転する。</summary>
public sealed class BoolNegation : IValueConverter
{
    public static BoolNegation Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>true なら隠す（BooleanToVisibilityConverter の逆）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
