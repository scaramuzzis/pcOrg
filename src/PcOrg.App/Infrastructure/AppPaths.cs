using System.IO;

namespace PcOrg.App.Infrastructure;

/// <summary>
/// Dove salvare i dati. L'app è un eseguibile da copiare: se accanto all'eseguibile esiste
/// la cartella "pcOrg-dati" (modalità portatile, es. su chiavetta) usa quella,
/// altrimenti %LOCALAPPDATA%\pcOrg.
/// </summary>
public static class AppPaths
{
    public const string PortableFolderName = "pcOrg-dati";

    public static string DataFolder { get; } = ResolveDataFolder();

    public static string DatabaseFile => Path.Combine(DataFolder, "indice.db");

    private static string ResolveDataFolder()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, PortableFolderName);
        if (Directory.Exists(portable))
            return portable;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pcOrg");
    }
}
