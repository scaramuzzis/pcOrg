using System.IO;
using PcOrg.Core.Diagnostics;

namespace PcOrg.App.Infrastructure;

/// <summary>
/// Rileva se l'ultima sessione si è chiusa male. All'avvio si crea il file "sessione.lock" nella cartella
/// dei dati, alla chiusura regolare lo si cancella: se all'avvio successivo c'è ancora, l'app si era
/// bloccata o era stata chiusa a forza.
/// </summary>
public static class SessionGuard
{
    private static string LockFile => Path.Combine(AppPaths.DataFolder, "sessione.lock");

    public static bool PreviousSessionCrashed()
    {
        try
        {
            return File.Exists(LockFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Begin()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            File.WriteAllText(LockFile, $"{DateTime.Now:O} pid {Environment.ProcessId}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Impossibile creare il file di sessione.", ex);
        }
    }

    public static void End()
    {
        try
        {
            File.Delete(LockFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
