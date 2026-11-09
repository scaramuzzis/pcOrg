namespace PcOrg.Core.Moving;

public enum MoveItemStatus
{
    /// <summary>Il file verrà spostato con lo stesso nome.</summary>
    Pronto,
    /// <summary>Esiste già un file con quel nome: verrà spostato con un nome diverso, es. "setup (2).exe".</summary>
    Rinominato,
    /// <summary>Il file è già nella cartella di destinazione.</summary>
    GiaPresente,
    /// <summary>Il file non esiste più nella posizione indicata.</summary>
    NonTrovato,
}

/// <summary>Uno spostamento proposto, da mostrare in anteprima prima di eseguirlo.</summary>
public sealed record MoveItem(string Source, string Destination, MoveItemStatus Status)
{
    public bool WillMove => Status is MoveItemStatus.Pronto or MoveItemStatus.Rinominato;

    public string StatusText => Status switch
    {
        MoveItemStatus.Pronto => "Da spostare",
        MoveItemStatus.Rinominato => "Da spostare con nuovo nome",
        MoveItemStatus.GiaPresente => "Già nella destinazione",
        MoveItemStatus.NonTrovato => "File non trovato",
        _ => Status.ToString(),
    };
}

/// <summary>Prepara l'elenco degli spostamenti senza toccare alcun file.</summary>
public static class MovePlanner
{
    public static IReadOnlyList<MoveItem> Plan(IEnumerable<string> sources, string destinationFolder)
    {
        var destination = PathUtil.Normalize(destinationFolder);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<MoveItem>();

        foreach (var raw in sources)
        {
            var source = PathUtil.Normalize(raw);
            if (!File.Exists(source))
            {
                plan.Add(new MoveItem(source, source, MoveItemStatus.NonTrovato));
                continue;
            }
            if (string.Equals(Path.GetDirectoryName(source), destination, PathUtil.Comparison))
            {
                plan.Add(new MoveItem(source, source, MoveItemStatus.GiaPresente));
                continue;
            }

            var target = Path.Combine(destination, Path.GetFileName(source));
            var status = MoveItemStatus.Pronto;
            if (IsTaken(target, reserved))
            {
                target = FreeName(destination, Path.GetFileName(source), reserved);
                status = MoveItemStatus.Rinominato;
            }
            reserved.Add(target);
            plan.Add(new MoveItem(source, target, status));
        }
        return plan;
    }

    private static bool IsTaken(string path, HashSet<string> reserved) =>
        reserved.Contains(path) || File.Exists(path) || Directory.Exists(path);

    /// <summary>Primo nome libero nella forma "nome (2).ext", "nome (3).ext", ...</summary>
    internal static string FreeName(string folder, string fileName, HashSet<string> reserved)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!IsTaken(candidate, reserved))
                return candidate;
        }
    }
}
