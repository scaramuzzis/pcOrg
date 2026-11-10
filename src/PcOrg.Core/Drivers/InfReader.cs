using System.Globalization;
using System.Text;

namespace PcOrg.Core.Drivers;

/// <summary>Identificativo hardware supportato da un driver, con la descrizione della periferica.</summary>
public sealed record DriverHardwareId(string Id, string? Description);

/// <summary>Dati principali di un file .inf (pacchetto driver).</summary>
public sealed record InfInfo(
    string? Class,
    string? Provider,
    string? Version,
    DateTime? Date,
    IReadOnlyList<DriverHardwareId> HardwareIds);

/// <summary>
/// Lettore dei file .inf dei driver di Windows.
/// </summary>
/// <remarks>
/// Un .inf è un file di testo a sezioni ([Version], [Manufacturer], [Strings]...). Da qui si leggono:
/// <list type="bullet">
/// <item><b>[Version]</b>: Class (tipo di periferica), Provider (chi distribuisce il driver), DriverVer (data e versione);</item>
/// <item><b>[Manufacturer]</b>: l'elenco delle sezioni "modelli", anche con le decorazioni di piattaforma
/// (es. <c>Realtek = Realtek, NTamd64</c> → sezioni <c>Realtek</c> e <c>Realtek.NTamd64</c>);</item>
/// <item><b>sezioni modelli</b>: righe <c>%Descrizione% = SezioneInstallazione, ID1, ID2...</c> con gli
/// identificativi hardware supportati;</item>
/// <item><b>[Strings]</b>: i valori dei riferimenti <c>%Nome%</c>.</item>
/// </list>
/// I file possono essere in UTF-16 (frequente) oppure in ANSI/UTF-8. Si leggono al massimo 8 MB.
/// </remarks>
public static class InfReader
{
    private const int MaxBytes = 8 * 1024 * 1024;

    public static InfInfo? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes)
                return null;
            return Parse(Decode(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[1] == 0)
            return Encoding.Unicode.GetString(bytes);
        return Encoding.Latin1.GetString(bytes);
    }

    public static InfInfo? Parse(string text)
    {
        var sections = ReadSections(text);
        if (!sections.TryGetValue("Version", out var version))
            return null;

        // [Strings], eventualmente localizzate (es. [Strings.0410]): si usa prima quella generica.
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, lines) in sections.OrderBy(s => s.Key.Length))
            if (name.Equals("Strings", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Strings.", StringComparison.OrdinalIgnoreCase))
                foreach (var (key, value) in lines.Select(SplitKeyValue).Where(kv => kv.Key is not null))
                    strings.TryAdd(key!, Unquote(value));

        string? Resolve(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            value = Unquote(value.Trim());
            if (value.Length > 2 && value[0] == '%' && value[^1] == '%')
                return strings.TryGetValue(value[1..^1], out var resolved) ? resolved : value.Trim('%');
            return value;
        }

        var versionValues = version.Select(SplitKeyValue).Where(kv => kv.Key is not null)
            .GroupBy(kv => kv.Key!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        var cls = Resolve(versionValues.GetValueOrDefault("Class"));
        var provider = Resolve(versionValues.GetValueOrDefault("Provider"));
        string? driverVersion = null;
        DateTime? date = null;
        if (versionValues.GetValueOrDefault("DriverVer") is { } driverVer)
        {
            var parts = driverVer.Split(',', 2, StringSplitOptions.TrimEntries);
            if (DateTime.TryParseExact(parts[0], ["MM/dd/yyyy", "M/d/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                date = d;
            if (parts.Length > 1 && parts[1].Length > 0)
                driverVersion = parts[1];
        }

        // Sezioni dei modelli elencate in [Manufacturer]
        var modelSections = new List<string>();
        if (sections.TryGetValue("Manufacturer", out var manufacturers))
        {
            foreach (var (_, value) in manufacturers.Select(SplitKeyValue))
            {
                var items = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (items.Length == 0)
                    continue;
                var baseName = Unquote(items[0]);
                modelSections.Add(baseName);
                foreach (var decoration in items.Skip(1))
                    modelSections.Add($"{baseName}.{decoration}");
            }
        }

        var ids = new List<DriverHardwareId>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in modelSections)
        {
            if (!sections.TryGetValue(model, out var lines))
                continue;
            foreach (var (key, value) in lines.Select(SplitKeyValue))
            {
                var items = value.Split(',', StringSplitOptions.TrimEntries);
                var description = Resolve(key);
                foreach (var id in items.Skip(1).Select(Unquote).Where(i => i.Length > 0 && i.Contains('\\') || i.StartsWith('*')))
                    if (seen.Add(id))
                        ids.Add(new DriverHardwareId(id, description));
            }
        }
        return new InfInfo(cls, provider, driverVersion, date, ids);
    }

    /// <summary>Divide il testo in sezioni, togliendo commenti e unendo le righe spezzate con "\".</summary>
    private static Dictionary<string, List<string>> ReadSections(string text)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        var pending = new StringBuilder();
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.EndsWith('\\'))
            {
                pending.Append(line[..^1]);
                continue;
            }
            if (pending.Length > 0)
            {
                line = pending.Append(line).ToString();
                pending.Clear();
            }
            if (line.Length == 0)
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (!sections.TryGetValue(name, out current))
                    sections[name] = current = [];
                continue;
            }
            current?.Add(line);
        }
        return sections;
    }

    /// <summary>Toglie il commento (";" fuori dalle virgolette).</summary>
    private static string StripComment(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                quoted = !quoted;
            else if (line[i] == ';' && !quoted)
                return line[..i];
        }
        return line;
    }

    private static (string? Key, string Value) SplitKeyValue(string line)
    {
        var eq = line.IndexOf('=');
        return eq < 0 ? (null, line.Trim()) : (line[..eq].Trim(), line[(eq + 1)..].Trim());
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value.Trim();
}
