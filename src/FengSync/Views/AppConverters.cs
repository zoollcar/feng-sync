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
    public static readonly IValueConverter SeverityForegroundConverter = new SeverityToForegroundConverter();
    public static readonly IValueConverter NullableBytesConverter = new NullableBytesConverter();
    public static readonly IValueConverter ModifiedConverter = new ModifiedUtcConverter();
    public static readonly IValueConverter BytesConverter = new BytesConverter();
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

internal sealed class SeverityToForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "Success" => "SuccessBrush",
            "Info" => "InfoBrush",
            "Danger" => "DangerBrush",
            "Warning" => "WarningBrush",
            _ => "TextSecondaryBrush",
        };
        return Application.Current?.Resources[key] is Brush brush ? brush : Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

internal sealed class NullableBytesConverter : IValueConverter
{
    // Display size as "1.8 GB" / "12 KB" / "—" when unknown. Rounds to two
    // significant digits above 1 KB and never drops below the byte for small
    // files. Returns "—" for null so a missing fingerprint is obvious.
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "—";
        if (value is long l) return SizeFormatter.Format(l);
        return "—";
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

internal sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is long l ? SizeFormatter.Format(l) : "0 B";

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

internal sealed class ModifiedUtcConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) : "—";

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

internal static class SizeFormatter
{
    // Mirrors MainWindow's legacy formatting so chip labels and summary text stay consistent.
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes:N0} B",
        < 1024 * 1024 => $"{bytes / 1024d:N1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024:N1} MB",
        _ => $"{bytes / 1024d / 1024 / 1024:N2} GB"
    };
}
