using System.Globalization;
using System.Windows.Data;

namespace PcOrg.App.Infrastructure;

/// <summary>Mostra una dimensione in byte in forma leggibile (es. "12,4 MB").</summary>
public sealed class FileSizeConverter : IValueConverter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} B"
            : size.ToString(size < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long bytes ? Format(bytes) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
