using System.Runtime.InteropServices;
using System.Text;

namespace PcOrg.Core;

/// <summary>Riconoscimento e normalizzazione delle cartelle di rete (\\server\condivisione, unità mappate).</summary>
public static class NetworkPaths
{
    /// <summary>Percorso UNC, es. \\NAS\Foto o \\192.168.1.10\Condivisa.</summary>
    public static bool IsUnc(string path) =>
        path.Length > 2 && (path[0] is '\\' or '/') && (path[1] is '\\' or '/') && path[2] is not ('?' or '.');

    /// <summary>Vero per percorsi UNC e per unità di rete mappate (es. Z:\ collegata a \\NAS\Dati).</summary>
    public static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        if (IsUnc(path))
            return true;
        if (!OperatingSystem.IsWindows() || path.Length < 2 || path[1] != ':')
            return false;
        try
        {
            return new DriveInfo(path[..1]).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Converte un'unità di rete mappata nel percorso UNC (Z:\Foto → \\NAS\Dati\Foto), così la cartella
    /// resta valida anche se la lettera dell'unità cambia o non è collegata all'avvio. Altri percorsi
    /// vengono restituiti invariati.
    /// </summary>
    public static string ToUnc(string path)
    {
        if (!OperatingSystem.IsWindows() || IsUnc(path) || path.Length < 2 || path[1] != ':')
            return path;
        var drive = path[..2];
        var buffer = new StringBuilder(512);
        var length = buffer.Capacity;
        if (WNetGetConnection(drive, buffer, ref length) != 0 || buffer.Length == 0)
            return path;
        var rest = path[2..].TrimStart('\\', '/');
        return rest.Length == 0 ? buffer.ToString() : Path.Combine(buffer.ToString(), rest);
    }

    /// <summary>
    /// Controlla se una cartella è raggiungibile senza bloccarsi a lungo: su una condivisione spenta
    /// Windows può impiegare molti secondi prima di rispondere.
    /// </summary>
    public static async Task<bool> IsReachableAsync(string path, TimeSpan timeout)
    {
        var check = Task.Run(() => Directory.Exists(path));
        var finished = await Task.WhenAny(check, Task.Delay(timeout)).ConfigureAwait(false);
        return finished == check && check.Result;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetGetConnectionW")]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
