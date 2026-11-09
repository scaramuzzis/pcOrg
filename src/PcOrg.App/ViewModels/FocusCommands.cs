using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;

namespace PcOrg.App.ViewModels;

public static class FocusCommands
{
    public static readonly RoutedCommand FocusSearch = new(nameof(FocusSearch), typeof(FocusCommands));
}

/// <summary>Le date sono salvate in UTC: in interfaccia si mostrano in ora locale.</summary>
public sealed class UtcToLocalConverter : IValueConverter
{
    public static UtcToLocalConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTime utc ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime() : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
