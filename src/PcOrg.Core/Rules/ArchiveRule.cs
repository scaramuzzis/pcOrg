using System.Globalization;
using System.Text;
using PcOrg.Core.Classification;
using PcOrg.Core.Moving;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Rules;

/// <summary>
/// Regola di archiviazione: "i file che rispondono a queste condizioni vanno in questa cartella".
/// Le condizioni vuote non vengono considerate; tutte quelle indicate devono essere vere.
/// </summary>
public sealed class ArchiveRule
{
    public string Name { get; set; } = "Nuova regola";
    public bool Enabled { get; set; } = true;

    /// <summary>Categoria richiesta (null = qualsiasi).</summary>
    public FileCategory? Category { get; set; }

    /// <summary>Il produttore deve contenere questo testo (es. "NVIDIA").</summary>
    public string? CompanyContains { get; set; }

    /// <summary>Il nome del file deve contenere questo testo (es. "driver").</summary>
    public string? NameContains { get; set; }

    /// <summary>
    /// Cartella di destinazione, con segnaposto facoltativi:
    /// {Categoria}, {Produttore}, {Prodotto}, {Versione}, {Anno}, {Mese}, {Estensione}.
    /// Esempio: D:\Archivio\Driver\{Produttore}.
    /// </summary>
    public string Destination { get; set; } = "";

    public bool Matches(FileRecord file)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(Destination))
            return false;
        if (Category is { } c && file.Category != c)
            return false;
        if (!string.IsNullOrWhiteSpace(CompanyContains)
            && !(file.Company ?? "").Contains(CompanyContains.Trim(), StringComparison.CurrentCultureIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(NameContains)
            && !file.Name.Contains(NameContains.Trim(), StringComparison.CurrentCultureIgnoreCase))
            return false;
        return true;
    }

    /// <summary>Descrizione leggibile delle condizioni, per l'elenco delle regole.</summary>
    public string ConditionText
    {
        get
        {
            var parts = new List<string>();
            if (Category is { } c) parts.Add($"categoria {c.DisplayName()}");
            if (!string.IsNullOrWhiteSpace(CompanyContains)) parts.Add($"produttore contiene \"{CompanyContains}\"");
            if (!string.IsNullOrWhiteSpace(NameContains)) parts.Add($"nome contiene \"{NameContains}\"");
            return parts.Count == 0 ? "tutti i file" : string.Join(", ", parts);
        }
    }
}

/// <summary>Applica le regole di archiviazione e prepara gli spostamenti (sempre da confermare in anteprima).</summary>
public static class RuleEngine
{
    public static readonly IReadOnlyList<string> Placeholders =
        ["{Categoria}", "{Produttore}", "{Prodotto}", "{Versione}", "{Anno}", "{Mese}", "{Estensione}"];

    /// <summary>Prima regola (nell'ordine dell'elenco) che vale per il file.</summary>
    public static ArchiveRule? FirstMatch(FileRecord file, IEnumerable<ArchiveRule> rules) => rules.FirstOrDefault(r => r.Matches(file));

    /// <summary>Cartella di destinazione con i segnaposto sostituiti dai dati del file.</summary>
    public static string ResolveDestination(ArchiveRule rule, FileRecord file)
    {
        var local = file.ModifiedUtc.ToLocalTime();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{Categoria}"] = file.CategoryName,
            ["{Produttore}"] = ShortCompany(file.Company) ?? "Produttore sconosciuto",
            ["{Prodotto}"] = file.ProductName ?? Path.GetFileNameWithoutExtension(file.Name),
            ["{Versione}"] = file.Version ?? "senza versione",
            ["{Anno}"] = local.Year.ToString(CultureInfo.InvariantCulture),
            ["{Mese}"] = local.ToString("MM", CultureInfo.InvariantCulture),
            ["{Estensione}"] = file.Extension.TrimStart('.').ToUpperInvariant(),
        };
        var result = rule.Destination.Trim();
        foreach (var (placeholder, value) in values)
            result = result.Replace(placeholder, SafeSegment(value), StringComparison.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>
    /// Piano di spostamento per i file indicati: ogni file va nella cartella della prima regola che lo
    /// riguarda. I file senza regola vengono ignorati. Il piano non tocca alcun file.
    /// </summary>
    public static (IReadOnlyList<MoveItem> Plan, int Unmatched) Plan(IEnumerable<FileRecord> files, IReadOnlyList<ArchiveRule> rules)
    {
        var targets = new List<(string Source, string Folder)>();
        var unmatched = 0;
        foreach (var file in files)
        {
            if (FirstMatch(file, rules) is { } rule)
                targets.Add((file.Path, ResolveDestination(rule, file)));
            else
                unmatched++;
        }
        return (MovePlanner.PlanMany(targets), unmatched);
    }

    /// <summary>"NVIDIA Corporation" → "NVIDIA": nomi di cartella più puliti.</summary>
    private static string? ShortCompany(string? company)
    {
        if (string.IsNullOrWhiteSpace(company))
            return null;
        var suffixes = new[] { " Corporation", " Corp.", " Corp", " Inc.", " Inc", ", Inc.", " Ltd.", " Ltd", " LLC", " GmbH", " S.r.l.", " S.p.A.", " Co., Ltd." };
        var name = company.Trim();
        foreach (var suffix in suffixes)
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^suffix.Length].TrimEnd(',', ' ');
        return name;
    }

    /// <summary>Toglie i caratteri non ammessi nei nomi di cartella di Windows.</summary>
    private static string SafeSegment(string value)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.ToString().Trim().TrimEnd('.');
    }
}
