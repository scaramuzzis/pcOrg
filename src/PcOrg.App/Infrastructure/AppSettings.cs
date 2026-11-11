using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Rules;

namespace PcOrg.App.Infrastructure;

/// <summary>Stato salvato di una colonna di una griglia: visibilità, posizione, larghezza e ordinamento.</summary>
public sealed class ColumnSetting
{
    /// <summary>Identifica la colonna: è il testo dell'intestazione (es. "Versione").</summary>
    public string Key { get; set; } = "";
    public bool Visible { get; set; } = true;
    public int DisplayIndex { get; set; }
    public double Width { get; set; }
    /// <summary>"Pixel", "Star" (proporzionale) o "Auto".</summary>
    public string WidthUnit { get; set; } = "Pixel";
    /// <summary>"Ascending", "Descending" o null se la griglia non è ordinata per questa colonna.</summary>
    public string? Sort { get; set; }
}

/// <summary>Posizione e dimensione della finestra principale.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// Preferenze dell'utente, salvate in settings.json nella cartella dei dati.
/// Il file è JSON leggibile: se viene danneggiato o cancellato l'app riparte con i valori predefiniti.
/// </summary>
public sealed class AppSettings
{
    // ---------------------------------------------------------------- Generale

    /// <summary>"Sistema", "Chiaro" o "Scuro".</summary>
    public string Theme { get; set; } = "Sistema";

    /// <summary>All'avvio aggiorna le cartelle monitorate, così l'elenco è sempre attuale.</summary>
    public bool ScanOnStartup { get; set; } = true;

    /// <summary>Verifica le firme digitali (più lento: legge ogni programma per intero).</summary>
    public bool VerifySignatures { get; set; } = true;

    /// <summary>Indicizza il testo dei documenti e il contenuto di ZIP e ISO, per poterli cercare.</summary>
    public bool IndexContent { get; set; } = true;

    /// <summary>La ricerca guarda anche dentro i file (pulsante "Nel contenuto").</summary>
    public bool SearchInContent { get; set; }

    /// <summary>All'avvio controlla su internet se esiste una versione più recente (disattivato di default).</summary>
    public bool CheckUpdatesOnStartup { get; set; }

    /// <summary>Rileva subito file aggiunti, modificati o eliminati mentre l'app è aperta.</summary>
    public bool WatchFolders { get; set; } = true;

    // ---------------------------------------------------------------- Registro attività

    /// <summary>Scrive il registro attività (log\pcorg.log).</summary>
    public bool LogEnabled { get; set; } = true;

    /// <summary>Livello di dettaglio del registro.</summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    // ---------------------------------------------------------------- Aspetto

    public bool ShowPreviewPanel { get; set; } = true;
    public bool ShowDetailsPanel { get; set; } = true;

    /// <summary>Larghezza del pannello di sinistra (cartelle e categorie).</summary>
    public double LeftPanelWidth { get; set; } = 300;

    public WindowPlacement? Window { get; set; }

    /// <summary>Regole di archiviazione, nell'ordine in cui vengono provate.</summary>
    public List<ArchiveRule> Rules { get; set; } = [];

    /// <summary>
    /// Categorie che l'utente ha scelto di non vedere nel pannello "Categorie" (nomi di
    /// <c>FileCategory</c>). Vuoto = si vedono tutte. I file restano indicizzati e ricercabili.
    /// </summary>
    public List<string> HiddenCategories { get; set; } = [];

    /// <summary>Colonne di ogni griglia, per nome della griglia ("file", "prodotti", "fileProdotto").</summary>
    public Dictionary<string, List<ColumnSetting>> Columns { get; set; } = [];

    // ---------------------------------------------------------------- Salvataggio

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Percorso del file da cui sono state lette le preferenze (null = non salvare).</summary>
    [JsonIgnore]
    public string? FilePath { get; private set; }

    /// <summary>Legge le preferenze; in caso di file mancante o danneggiato restituisce quelle predefinite.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        AppSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"Preferenze non leggibili ({path}): si usano quelle predefinite.", ex);
            settings = new AppSettings();
        }
        settings.FilePath = path;
        return settings;
    }

    /// <summary>Salva le preferenze. Scrive prima un file temporaneo, così un'interruzione non lo danneggia.</summary>
    public void Save()
    {
        if (FilePath is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Preferenze non salvate ({FilePath}).", ex);
        }
    }

    public static System.Windows.ThemeMode ToThemeMode(string theme) => theme switch
    {
        "Chiaro" => System.Windows.ThemeMode.Light,
        "Scuro" => System.Windows.ThemeMode.Dark,
        _ => System.Windows.ThemeMode.System,
    };
}
