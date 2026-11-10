using System.Globalization;
using System.Windows.Data;
using PcOrg.Core.Drivers;
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

    /// <summary>Periferiche presenti, per il confronto con i driver dell'archivio.</summary>
    public static DriverMatcher Devices { get; set; } = DriverMatcher.Empty;

    /// <summary>Esito del confronto per ogni pacchetto driver (.inf), per id del file.</summary>
    public static IReadOnlyDictionary<long, DriverMatch> Drivers { get; set; } = new Dictionary<long, DriverMatch>();

    /// <summary>
    /// Esito "migliore" per cartella: i file di un pacchetto driver (.sys, .cat, .dll, setup.exe) stanno
    /// accanto al loro .inf e ne condividono lo stato.
    /// </summary>
    public static IReadOnlyDictionary<string, DriverMatch> DriverFolders { get; set; } = new Dictionary<string, DriverMatch>();

    /// <summary>Testo della colonna "Sul PC" per un file.</summary>
    public static string StatusOf(FileRecord file)
    {
        if (Drivers.TryGetValue(file.Id, out var match))
            return match.Text;
        if (file.Category == Core.Classification.FileCategory.Driver && DriverFolders.TryGetValue(file.Folder, out var folder))
            return folder.Text;
        return Current.Match(file).Text;
    }
}

/// <summary>File → stato sul PC: per gli installer il programma installato, per i driver le periferiche presenti.</summary>
public sealed class InstallStateConverter : IValueConverter
{
    public static InstallStateConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        FileRecord file => InstalledCatalog.StatusOf(file),
        ProductGroup group when group.Files.Count > 0 => InstalledCatalog.StatusOf(group.Files[0]),
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
