using PcOrg.Core.Storage;

namespace PcOrg.Core.Duplicates;

/// <summary>Quale copia tenere in ogni gruppo di cloni quando si segnano i doppioni.</summary>
public enum KeepRule
{
    /// <summary>La copia con la data di modifica più vecchia (di solito l'originale).</summary>
    PiuVecchio,
    /// <summary>La copia modificata più di recente.</summary>
    PiuRecente,
    /// <summary>La copia con il percorso più corto (di solito quella meglio archiviata).</summary>
    PercorsoPiuCorto,
    /// <summary>La copia con il percorso più lungo (es. dentro una struttura di archivio).</summary>
    PercorsoPiuLungo,
}

/// <summary>Regole sicure per scegliere i doppioni da eliminare: in ogni gruppo resta sempre almeno una copia.</summary>
public static class CleanupRules
{
    public static string Describe(KeepRule rule) => rule switch
    {
        KeepRule.PiuRecente => "Tieni la copia più recente",
        KeepRule.PercorsoPiuCorto => "Tieni la copia con il percorso più corto",
        KeepRule.PercorsoPiuLungo => "Tieni la copia con il percorso più lungo",
        _ => "Tieni la copia più vecchia (l'originale)",
    };

    /// <summary>La copia da tenere secondo la regola.</summary>
    public static FileRecord Keeper(DuplicateGroup group, KeepRule rule) => rule switch
    {
        KeepRule.PiuRecente => group.Files.MaxBy(f => f.ModifiedUtc)!,
        KeepRule.PercorsoPiuCorto => group.Files.MinBy(f => f.Path.Length)!,
        KeepRule.PercorsoPiuLungo => group.Files.MaxBy(f => f.Path.Length)!,
        _ => group.Files.MinBy(f => f.ModifiedUtc)!,
    };

    /// <summary>Tutti i file da segnare come doppioni (tutte le copie tranne quella da tenere).</summary>
    public static IEnumerable<FileRecord> Doubles(IEnumerable<DuplicateGroup> groups, KeepRule rule) =>
        groups.SelectMany(g =>
        {
            var keep = Keeper(g, rule);
            return g.Files.Where(f => f.Id != keep.Id);
        });

    /// <summary>
    /// Controllo di sicurezza prima di eliminare: restituisce i gruppi di cui sono state scelte
    /// <b>tutte</b> le copie (che non vanno eliminate, altrimenti il file andrebbe perso).
    /// </summary>
    public static IReadOnlyList<DuplicateGroup> GroupsLosingAllCopies(IEnumerable<DuplicateGroup> groups, IReadOnlySet<long> selectedIds) =>
        groups.Where(g => g.Files.All(f => selectedIds.Contains(f.Id))).ToList();
}
