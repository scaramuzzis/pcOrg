using System.Globalization;
using System.Windows.Data;
using PcOrg.Core.Installed;
using PcOrg.Core.Metadata;
using PcOrg.Core.Storage;

namespace PcOrg.App.Infrastructure;

/// <summary>
/// Elenco dei programmi installati in uso nell'interfaccia. Viene letto in background all'avvio
/// e a ogni aggiornamento; le colonne "Sul PC" lo consultano tramite i convertitori qui sotto.
/// </summary>
public static class InstalledCatalog
{
    public static InstalledIndex Current { get; set; } = InstalledIndex.Empty;
}

/// <summary>File → stato di installazione ("Non installato", "Installata 3.0.20: questo è più recente"...).</summary>
public sealed class InstallStateConverter : IValueConverter
{
    public static InstallStateConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        FileRecord file => InstalledCatalog.Current.Match(file).Text,
        ProductGroup group when group.Files.Count > 0 => InstalledCatalog.Current.Match(group.Files[0]).Text,
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
