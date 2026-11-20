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
    /// <summary>Regola proposta: si tiene la copia più recente e si eliminano le più vecchie.</summary>
    public const KeepRule DefaultRule = KeepRule.PiuRecente;

    /// <summary>Regole nell'ordine in cui proporle (la predefinita per prima).</summary>
    public static IReadOnlyList<KeepRule> Ordered { get; } =
        [KeepRule.PiuRecente, KeepRule.PiuVecchio, KeepRule.PercorsoPiuCorto, KeepRule.PercorsoPiuLungo];

    public static string Describe(KeepRule rule) => rule switch
    {
        KeepRule.PiuRecente => "Tieni la copia più recente (elimina le più vecchie)",
        KeepRule.PercorsoPiuCorto => "Tieni la copia con il percorso più corto",
        KeepRule.PercorsoPiuLungo => "Tieni la copia con il percorso più lungo",
        _ => "Tieni la copia più vecchia (elimina le più recenti)",
    };

    /// <summary>
    /// Data di creazione di un file (null se non leggibile). Serve quando le copie hanno la stessa data di
    /// modifica, cosa normale: copiando un file Windows mantiene la data di modifica dell'originale, mentre la
    /// data di creazione è quella della copia. Sostituibile nei test.
    /// </summary>
    public static Func<string, DateTime?> CreationTime { get; set; } = path =>
    {
        try
        {
            return File.Exists(path) ? File.GetCreationTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    };

    /// <summary>
    /// La copia da tenere secondo la regola. "Più vecchia/recente" guarda la data di modifica e, a parità,
    /// quella di creazione (la copia fatta dopo è più recente); poi il percorso, così la scelta non è mai casuale.
    /// </summary>
    public static FileRecord Keeper(DuplicateGroup group, KeepRule rule)
    {
        var files = group.Files;
        switch (rule)
        {
            case KeepRule.PercorsoPiuCorto:
                return files.OrderBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First();
            case KeepRule.PercorsoPiuLungo:
                return files.OrderByDescending(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First();
        }
        // Le date di creazione si leggono solo se servono (copie con la stessa data di modifica).
        var created = new Dictionary<long, DateTime?>();
        DateTime? Created(FileRecord f) => created.TryGetValue(f.Id, out var c) ? c : created[f.Id] = CreationTime(f.Path);
        return rule == KeepRule.PiuRecente
            ? files.OrderByDescending(f => f.ModifiedUtc).ThenByDescending(f => Created(f) ?? DateTime.MinValue)
                   .ThenBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First()
            : files.OrderBy(f => f.ModifiedUtc).ThenBy(f => Created(f) ?? DateTime.MaxValue)
                   .ThenBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First();
    }

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
