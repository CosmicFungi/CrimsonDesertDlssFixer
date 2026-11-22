using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CrimsonDesertDlssFixer.Models;

namespace CrimsonDesertDlssFixer.Converters;

public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IssueSeverity sev)
        {
            return sev switch
            {
                IssueSeverity.Critical => new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                IssueSeverity.Warning => new SolidColorBrush(Color.FromRgb(217, 119, 6)),
                IssueSeverity.Info => new SolidColorBrush(Color.FromRgb(2, 132, 199)),
                IssueSeverity.Ok => new SolidColorBrush(Color.FromRgb(5, 150, 105)),
                _ => new SolidColorBrush(Color.FromRgb(71, 85, 105))
            };
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class FixStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "FIXED" => new SolidColorBrush(Color.FromRgb(5, 150, 105)),
            "FAILED" => new SolidColorBrush(Color.FromRgb(220, 38, 38)),
            _ => Brushes.Transparent
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
