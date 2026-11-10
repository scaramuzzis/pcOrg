using System.IO;
using System.Text.Json;

namespace PcOrg.App.Infrastructure;

/// <summary>Preferenze dell'utente (tema, pannelli), salvate in settings.json nella cartella dei dati.</summary>
public sealed class AppSettings
{
    /// <summary>"Sistema", "Chiaro" o "Scuro".</summary>
    public string Theme { get; set; } = "Sistema";
    public bool ShowPreviewPanel { get; set; } = true;
    public bool ShowDetailsPanel { get; set; } = true;

    private static string FilePath => Path.Combine(AppPaths.DataFolder, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferenze non salvate: non è un problema bloccante.
        }
    }

    public static System.Windows.ThemeMode ToThemeMode(string theme) => theme switch
    {
        "Chiaro" => System.Windows.ThemeMode.Light,
        "Scuro" => System.Windows.ThemeMode.Dark,
        _ => System.Windows.ThemeMode.System,
    };
}
