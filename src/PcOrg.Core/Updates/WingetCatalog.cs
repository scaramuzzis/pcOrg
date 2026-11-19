using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Metadata;

namespace PcOrg.Core.Updates;

/// <summary>Un pacchetto del catalogo pubblico di winget.</summary>
/// <param name="AlternativeNames">Altri nomi con cui il pacchetto è noto (tabella norm_names dell'indice).</param>
public sealed record WingetPackage(string Id, string Name, string LatestVersion, string? Publisher = null,
    IReadOnlyList<string>? AlternativeNames = null);

/// <summary>Prodotto riconosciuto nel catalogo winget, con l'ultima versione pubblicata.</summary>
/// <param name="IsNewer">Vero se la versione pubblicata è più recente della più recente trovata sul PC.</param>
public sealed record WingetMatch(string Id, string Name, string LatestVersion, bool IsNewer)
{
    public string Text => IsNewer ? $"Disponibile la {LatestVersion}" : $"Aggiornato ({LatestVersion})";
}

/// <summary>
/// Catalogo dei programmi di winget (il gestore di pacchetti di Windows), usato per sapere se dei
/// programmi trovati sul PC esiste una versione più recente. Si scarica l'indice pubblico
/// (cdn.winget.microsoft.com, lo stesso che usa winget) solo su richiesta: non viene inviato nessun dato
/// sui file del PC, il confronto avviene in locale.
/// </summary>
public sealed partial class WingetCatalog
{
    /// <summary>Indice attuale (formato 2) e precedente (formato 1), entrambi pacchetti msix (zip).</summary>
    public static readonly string[] SourceUrls =
    [
        "https://cdn.winget.microsoft.com/cache/source2.msix",
        "https://cdn.winget.microsoft.com/cache/source.msix",
    ];

    public const string IndexFileName = "winget-index.db";

    /// <summary>Dopo questo tempo l'indice scaricato si considera vecchio e viene riscaricato.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    private readonly Dictionary<string, List<WingetPackage>> _byName = new(StringComparer.Ordinal);

    public int Count { get; }

    private WingetCatalog(IEnumerable<WingetPackage> packages)
    {
        var count = 0;
        foreach (var p in packages)
        {
            if (string.IsNullOrWhiteSpace(p.Name) || !IsUsableVersion(p.LatestVersion))
                continue;
            count++;
            foreach (var key in KeysOf(p))
            {
                if (!_byName.TryGetValue(key, out var list))
                    _byName[key] = list = [];
                list.Add(p);
            }
        }
        Count = count;
    }

    public static WingetCatalog FromPackages(IEnumerable<WingetPackage> packages) => new(packages);

    /// <summary>
    /// Cerca un prodotto nel catalogo. Serve il nome; il produttore, se noto, deve corrispondere
    /// all'editore del pacchetto (evita di confondere programmi diversi con lo stesso nome).
    /// </summary>
    public WingetMatch? Match(string? productName, string? company, string? localVersion)
    {
        if (string.IsNullOrWhiteSpace(productName))
            return null;
        var name = Normalize(productName);
        if (name.Length < 3)
            return null;
        var companyWord = CompanyWord(company);

        var candidates = new HashSet<WingetPackage>();
        AddCandidates(candidates, name);
        if (companyWord is not null && !name.StartsWith(companyWord, StringComparison.Ordinal))
            AddCandidates(candidates, companyWord + name);
        if (candidates.Count == 0)
            return null;

        List<WingetPackage> chosen;
        if (companyWord is not null)
        {
            chosen = candidates.Where(p => PublisherMatches(p, companyWord)).ToList();
            if (chosen.Count == 0)
                return null;
        }
        else
        {
            if (candidates.Count != 1)
                return null; // senza produttore si accetta solo una corrispondenza univoca
            chosen = [.. candidates];
        }

        // Più rami dello stesso prodotto (es. "Python 3.13" e "Python 3.14"): si preferisce quello
        // il cui numero nel nome corrisponde alla versione presente sul PC.
        if (chosen.Count > 1 && VersionComparer.Clean(localVersion) is { } local)
        {
            var sameBranch = chosen.Where(p => BranchOf(p.Name) is { } b && (local == b || local.StartsWith(b + ".", StringComparison.Ordinal))).ToList();
            if (sameBranch.Count > 0)
                chosen = sameBranch;
        }
        var best = chosen.OrderByDescending(p => p.LatestVersion, VersionComparer.Instance).First();
        var newer = localVersion is not null && VersionComparer.Instance.Compare(best.LatestVersion, localVersion) > 0;
        return new WingetMatch(best.Id, best.Name, best.LatestVersion, newer);
    }

    private void AddCandidates(HashSet<WingetPackage> set, string key)
    {
        if (_byName.TryGetValue(key, out var list))
            set.UnionWith(list);
    }

    private static bool PublisherMatches(WingetPackage p, string companyWord)
    {
        var idPrefix = Normalize(p.Id.Split('.')[0]);
        if (idPrefix.Length > 0 && (idPrefix.StartsWith(companyWord, StringComparison.Ordinal) || companyWord.StartsWith(idPrefix, StringComparison.Ordinal)))
            return true;
        return p.Publisher is not null && Normalize(p.Publisher).Contains(companyWord, StringComparison.Ordinal);
    }

    /// <summary>Chiavi con cui un pacchetto viene trovato: il nome, e il nome senza l'editore davanti.</summary>
    private static IEnumerable<string> KeysOf(WingetPackage p)
    {
        var prefixes = new[] { Normalize(p.Id.Split('.')[0]), p.Publisher is null ? "" : CompanyWord(p.Publisher) ?? "" }
            .Where(x => x.Length > 0).Distinct().ToList();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in (p.AlternativeNames ?? []).Prepend(p.Name))
        {
            var name = Normalize(raw);
            if (name.Length == 0 || !keys.Add(name))
                continue;
            foreach (var prefix in prefixes)
                if (name.Length > prefix.Length + 2 && name.StartsWith(prefix, StringComparison.Ordinal))
                    keys.Add(name[prefix.Length..]);
        }
        return keys;
    }

    /// <summary>
    /// Forma confrontabile di un nome: minuscolo, senza numeri di versione, architettura e punteggiatura
    /// ("Python 3.14.10 (64-bit)" → "python", "Node.js" → "nodejs").
    /// </summary>
    public static string Normalize(string value)
    {
        var lower = value.ToLowerInvariant();
        // Lingua, architettura e varianti tra parentesi: "Mozilla Firefox (en-US)" → "mozilla firefox".
        lower = ParenthesesRegex().Replace(lower, " ");
        lower = NoiseRegex().Replace(lower, " ");
        var sb = new StringBuilder(lower.Length);
        foreach (var c in lower)
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Parola significativa del produttore ("Mozilla Corporation" → "mozilla").</summary>
    public static string? CompanyWord(string? company)
    {
        if (string.IsNullOrWhiteSpace(company))
            return null;
        foreach (var raw in company.Split([' ', ',', '.', '-', '(', ')', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = Normalize(raw);
            if (word.Length >= 2 && !CompanyNoise.Contains(word))
                return word;
        }
        return null;
    }

    private static readonly HashSet<string> CompanyNoise = ["the", "inc", "ltd", "llc", "gmbh", "corp", "corporation", "co", "srl", "spa"];

    private static string? BranchOf(string name)
    {
        var m = BranchRegex().Match(name);
        return m.Success ? m.Value : null;
    }

    private static bool IsUsableVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version) && !version.Equals("unknown", StringComparison.OrdinalIgnoreCase) && char.IsDigit(version[0]);

    [GeneratedRegex(@"\(?\b(x64|x86|amd64|arm64|64[- ]?bit|32[- ]?bit|win64|win32)\b\)?|\bv?\d+(\.\d+)*\b")]
    private static partial Regex NoiseRegex();

    [GeneratedRegex(@"\d+(\.\d+)+|\d+")]
    private static partial Regex BranchRegex();

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex ParenthesesRegex();

    // ------------------------------------------------------------------ Lettura dell'indice

    /// <summary>
    /// Legge l'indice di winget (SQLite). Gestisce sia il formato 2 (tabella packages con latest_version)
    /// sia il formato 1 (tabella manifest con una riga per versione).
    /// </summary>
    public static WingetCatalog Load(string dbFile)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbFile,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        var tables = Tables(connection);
        var packages = tables.Contains("packages") ? ReadV2(connection, tables) : ReadV1(connection, tables);
        Log.Info($"Catalogo winget letto: {packages.Count:N0} pacchetti.");
        return new WingetCatalog(packages);
    }

    private static HashSet<string> Tables(SqliteConnection connection)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            set.Add(reader.GetString(0));
        return set;
    }

    private static List<string> Columns(SqliteConnection connection, string table)
    {
        var list = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(reader.GetString(1));
        return list;
    }

    private static List<WingetPackage> ReadV2(SqliteConnection connection, HashSet<string> tables)
    {
        var publishers = ReadPublishers(connection, tables);
        var names = ReadNames(connection, tables);
        var list = new List<WingetPackage>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT rowid, id, name, latest_version FROM packages";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3))
                continue;
            publishers.TryGetValue(reader.GetInt64(0), out var publisher);
            names.TryGetValue(reader.GetInt64(0), out var alternative);
            list.Add(new WingetPackage(Convert.ToString(reader.GetValue(1))!, Convert.ToString(reader.GetValue(2))!,
                Convert.ToString(reader.GetValue(3))!, publisher, alternative));
        }
        return list;
    }

    /// <summary>Editori normalizzati per pacchetto (tabella norm_publishers*), se presenti.</summary>
    private static Dictionary<long, string> ReadPublishers(SqliteConnection connection, HashSet<string> tables)
    {
        var map = new Dictionary<long, string>();
        var table = tables.FirstOrDefault(t => t.StartsWith("norm_publishers", StringComparison.OrdinalIgnoreCase)
                                                && !t.EndsWith("_map", StringComparison.OrdinalIgnoreCase));
        if (table is null)
            return map;
        try
        {
            var columns = Columns(connection, table);
            var package = columns.FirstOrDefault(c => c.Contains("package", StringComparison.OrdinalIgnoreCase));
            var value = columns.FirstOrDefault(c => c.Contains("publisher", StringComparison.OrdinalIgnoreCase));
            if (package is null || value is null)
                return map;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT \"{package}\", \"{value}\" FROM \"{table}\"";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                    map.TryAdd(reader.GetInt64(0), Convert.ToString(reader.GetValue(1))!);
        }
        catch (SqliteException ex)
        {
            Log.Warn("Catalogo winget: editori non leggibili.", ex);
        }
        return map;
    }

    /// <summary>Nomi normalizzati da winget per pacchetto (tabella norm_names*), se presenti.</summary>
    private static Dictionary<long, List<string>> ReadNames(SqliteConnection connection, HashSet<string> tables)
    {
        var map = new Dictionary<long, List<string>>();
        var table = tables.FirstOrDefault(t => t.StartsWith("norm_names", StringComparison.OrdinalIgnoreCase)
                                                && !t.EndsWith("_map", StringComparison.OrdinalIgnoreCase));
        if (table is null)
            return map;
        try
        {
            var columns = Columns(connection, table);
            var package = columns.FirstOrDefault(c => c.Contains("package", StringComparison.OrdinalIgnoreCase));
            var value = columns.FirstOrDefault(c => c.Contains("name", StringComparison.OrdinalIgnoreCase));
            if (package is null || value is null)
                return map;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT \"{package}\", \"{value}\" FROM \"{table}\"";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                {
                    if (!map.TryGetValue(reader.GetInt64(0), out var list))
                        map[reader.GetInt64(0)] = list = [];
                    list.Add(Convert.ToString(reader.GetValue(1))!);
                }
        }
        catch (SqliteException ex)
        {
            Log.Warn("Catalogo winget: nomi alternativi non leggibili.", ex);
        }
        return map;
    }

    private static List<WingetPackage> ReadV1(SqliteConnection connection, HashSet<string> tables)
    {
        if (!tables.Contains("manifest") || !tables.Contains("ids") || !tables.Contains("names") || !tables.Contains("versions"))
            throw new InvalidDataException("Formato dell'indice di winget non riconosciuto.");
        var latest = new Dictionary<string, WingetPackage>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT i.id, n.name, v.version
            FROM manifest m
            JOIN ids i ON i.rowid = m.id
            JOIN names n ON n.rowid = m.name
            JOIN versions v ON v.rowid = m.version
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var p = new WingetPackage(reader.GetString(0), reader.GetString(1), reader.GetString(2));
            if (!IsUsableVersion(p.LatestVersion))
                continue;
            if (!latest.TryGetValue(p.Id, out var current) || VersionComparer.Instance.Compare(p.LatestVersion, current.LatestVersion) > 0)
                latest[p.Id] = p;
        }
        return [.. latest.Values];
    }

    // ------------------------------------------------------------------ Download

    /// <summary>
    /// Restituisce il percorso dell'indice di winget in <paramref name="folder"/>, scaricandolo se manca
    /// o se è più vecchio di un giorno. Se il download non riesce ma c'è una copia precedente, usa quella.
    /// </summary>
    public static async Task<string> EnsureIndexAsync(string folder, string appVersion, IProgress<double>? progress = null,
        HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, IndexFileName);
        if (File.Exists(target) && DateTime.UtcNow - File.GetLastWriteTimeUtc(target) < MaxAge)
            return target;

        var own = client is null;
        client ??= new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        Exception? last = null;
        try
        {
            foreach (var url in SourceUrls)
            {
                var package = Path.Combine(folder, "winget-source.msix");
                try
                {
                    await DownloadFileAsync(client, url, package, appVersion, progress, cancellationToken).ConfigureAwait(false);
                    using (var zip = ZipFile.OpenRead(package))
                    {
                        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals("Public/index.db", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("Il pacchetto di winget non contiene l'indice.");
                        var temp = target + ".nuovo";
                        entry.ExtractToFile(temp, overwrite: true);
                        File.Move(temp, target, overwrite: true);
                        File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
                    }
                    Log.Info($"Indice di winget scaricato da {url}.");
                    return target;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException
                                               && !cancellationToken.IsCancellationRequested)
                {
                    Log.Warn($"Indice di winget non scaricato da {url}.", ex);
                    last = ex;
                }
                finally
                {
                    try { File.Delete(package); } catch (IOException) { }
                }
            }
        }
        finally
        {
            if (own)
                client.Dispose();
        }
        if (File.Exists(target))
            return target; // meglio un indice di qualche giorno fa che niente
        throw new HttpRequestException("Il catalogo di winget non è raggiungibile.", last);
    }

    private static async Task DownloadFileAsync(HttpClient client, string url, string target, string appVersion,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("GestioneFile", appVersion));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            if (total > 0)
                progress?.Report(Math.Min(1.0, (double)written / total));
        }
    }
}
