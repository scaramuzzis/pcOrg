using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;

namespace PcOrg.App.ViewModels;

/// <summary>Comandi gestiti direttamente dalla finestra principale (focus, impostazioni).</summary>
public static class FocusCommands
{
    public static readonly RoutedCommand FocusSearch = new(nameof(FocusSearch), typeof(FocusCommands));

    /// <summary>Apre la finestra Impostazioni (gestita dalla finestra principale, che conosce le griglie).</summary>
    public static readonly RoutedCommand OpenSettings = new(nameof(OpenSettings), typeof(FocusCommands));
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

/// <summary>Nasconde un elemento quando il valore associato è null.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public static NullToCollapsedConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
