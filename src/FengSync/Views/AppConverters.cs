using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace FengSync.Views;

/// <summary>
/// Application-wide XAML value converters used by MainWindow and other views.
/// Kept here (rather than inside the views that consume them) so a single
/// instance serves every binding.
/// </summary>
public static class AppConverters
{
    public static readonly IValueConverter SeverityBackgroundConverter = new SeverityToBrushConverter();
}

internal sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "Success" => "SuccessSubtleBackgroundBrush",
            "Info" => "InfoSubtleBackgroundBrush",
            "Danger" => "DangerSubtleBackgroundBrush",
            "Warning" => "WarningSubtleBackgroundBrush",
            _ => "NeutralSubtleBackgroundBrush",
        };
        return Application.Current?.Resources[key] is Brush brush ? brush : Brushes.LightGray;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
