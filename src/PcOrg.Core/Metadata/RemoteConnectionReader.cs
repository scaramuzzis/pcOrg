using System.Text;
using System.Text.RegularExpressions;

namespace PcOrg.Core.Metadata;

/// <summary>
/// Legge da un file di connessione remota (VNC, Desktop remoto) l'indirizzo del computer di destinazione,
/// per mostrarlo nei dettagli e renderlo cercabile (es. cercando "192.168.1.20").
/// </summary>
/// <remarks>
/// Formati gestiti:
/// <list type="bullet">
/// <item>RealVNC: righe "Host=indirizzo:porta" (anche "ProxyServer", ignorato).</item>
/// <item>TightVNC / UltraVNC: sezione [connection] con "host=" e "port=".</item>
/// <item>Desktop remoto Microsoft (.rdp): "full address:s:indirizzo" e "username:s:utente".</item>
/// </list>
/// Si leggono al massimo 64 KB: questi file sono piccoli file di testo. Le password salvate
/// (cifrate) non vengono mai lette né mostrate.
/// </remarks>
public static partial class RemoteConnectionReader
{
    private const int MaxBytes = 64 * 1024;

    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".vnc", ".rdp" };

    [GeneratedRegex(@"^\s*host\s*=\s*(?<v>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex HostLine();

    [GeneratedRegex(@"^\s*port\s*=\s*(?<v>\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PortLine();

    [GeneratedRegex(@"^\s*full address:s:(?<v>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RdpAddress();

    [GeneratedRegex(@"^\s*username:s:(?<v>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RdpUser();

    public static FileMeta? Read(string path)
    {
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(MaxBytes, stream.Length)];
            stream.ReadExactly(buffer);
            text = Decode(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return Parse(text, Path.GetExtension(path));
    }

    /// <summary>I file .rdp sono spesso UTF-16; gli altri ASCII/UTF-8.</summary>
    private static string Decode(byte[] bytes) =>
        bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
        : bytes.Length >= 2 && bytes[1] == 0 ? Encoding.Unicode.GetString(bytes)
        : Encoding.UTF8.GetString(bytes);

    internal static FileMeta? Parse(string text, string extension)
    {
        if (extension.Equals(".rdp", StringComparison.OrdinalIgnoreCase))
        {
            var address = RdpAddress().Match(text);
            if (!address.Success)
                return new FileMeta { ProductName = "Desktop remoto" };
            var user = RdpUser().Match(text);
            return new FileMeta
            {
                ProductName = "Desktop remoto",
                FileDescription = $"Connessione Desktop remoto a {address.Groups["v"].Value}" +
                                  (user.Success ? $" (utente {user.Groups["v"].Value})" : ""),
            };
        }

        var host = HostLine().Match(text);
        if (!host.Success)
            return new FileMeta { ProductName = "VNC" };
        var target = host.Groups["v"].Value;
        var port = PortLine().Match(text);
        if (port.Success && !target.Contains(':'))
            target += ":" + port.Groups["v"].Value;
        return new FileMeta { ProductName = "VNC", FileDescription = $"Connessione VNC a {target}" };
    }
}
