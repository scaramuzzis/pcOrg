using System.Globalization;
using System.Text;

namespace PcOrg.Core.Diagnostics;

/// <summary>Livello di dettaglio del registro: si scrivono i messaggi di quel livello e dei livelli più gravi.</summary>
public enum LogLevel
{
    /// <summary>Tutto, compreso il dettaglio di ogni cartella aggiornata (utile per diagnosticare problemi).</summary>
    Debug = 0,
    /// <summary>Avvii, scansioni, spostamenti, errori (predefinito).</summary>
    Info = 1,
    /// <summary>Solo avvisi ed errori.</summary>
    Warning = 2,
    /// <summary>Solo errori.</summary>
    Error = 3,
}

/// <summary>
/// Registro delle attività su file di testo (pcorg.log nella cartella "log" dei dati).
/// Registra avvio, scansioni, spostamenti ed errori: serve a capire cosa è successo
/// quando qualcosa non va. È sicuro da usare da più thread e non lancia mai eccezioni.
/// </summary>
/// <remarks>
/// Quando il file supera <see cref="MaxBytes"/> viene rinominato in pcorg.1.log (e i precedenti
/// scalano fino a pcorg.<see cref="KeepFiles"/>.log), così il registro non cresce all'infinito.
/// Finché <see cref="Initialize"/> non viene chiamato le scritture vengono ignorate (es. nei test).
/// </remarks>
public static class Log
{
    public const long MaxBytes = 2 * 1024 * 1024;
    public const int KeepFiles = 3;

    private static readonly object Gate = new();
    private static string? _path;
    private static bool _enabled = true;
    private static LogLevel _minimum = LogLevel.Info;

    /// <summary>Percorso del file di registro attuale, o null se il registro non è attivo.</summary>
    public static string? FilePath => _path;

    /// <summary>Attiva il registro nella cartella indicata (creata se non esiste).</summary>
    public static void Initialize(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            _path = Path.Combine(folder, "pcorg.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _path = null; // senza registro l'app funziona comunque
        }
    }

    /// <summary>Attiva o disattiva il registro e ne sceglie il livello di dettaglio (dalle Impostazioni).</summary>
    public static void Configure(bool enabled, LogLevel minimum)
    {
        _enabled = enabled;
        _minimum = minimum;
    }

    public static bool Enabled => _enabled;
    public static LogLevel Minimum => _minimum;

    /// <summary>Vero se un messaggio di quel livello verrebbe scritto (evita di comporre testi inutili).</summary>
    public static bool IsEnabled(LogLevel level) => _enabled && _path is not null && level >= _minimum;

    public static void Debug(string message) => Write(LogLevel.Debug, "DETTAGLIO", message, null);
    public static void Info(string message) => Write(LogLevel.Info, "INFO", message, null);
    public static void Warn(string message, Exception? exception = null) => Write(LogLevel.Warning, "ATTENZIONE", message, exception);
    public static void Error(string message, Exception? exception = null) => Write(LogLevel.Error, "ERRORE", message, exception);

    /// <summary>Svuota il registro (e le copie precedenti).</summary>
    public static void Clear()
    {
        var path = _path;
        if (path is null)
            return;
        lock (Gate)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "pcorg*.log"))
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void Write(LogLevel severity, string level, string message, Exception? exception)
    {
        var path = _path;
        if (path is null || !IsEnabled(severity))
            return;
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ").Append(message);
        if (exception is not null)
            line.AppendLine().Append("    ").Append(exception.ToString().Replace(Environment.NewLine, Environment.NewLine + "    "));

        lock (Gate)
        {
            try
            {
                RotateIfNeeded(path);
                File.AppendAllText(path, line.AppendLine().ToString(), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Il registro non deve mai interrompere il programma.
            }
        }
    }

    private static void RotateIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxBytes)
            return;
        var folder = info.DirectoryName!;
        string Numbered(int n) => Path.Combine(folder, $"pcorg.{n}.log");
        if (File.Exists(Numbered(KeepFiles)))
            File.Delete(Numbered(KeepFiles));
        for (var n = KeepFiles - 1; n >= 1; n--)
            if (File.Exists(Numbered(n)))
                File.Move(Numbered(n), Numbered(n + 1));
        File.Move(path, Numbered(1));
    }
}
