using PcOrg.Core.Classification;
using PcOrg.Core.Metadata;

namespace PcOrg.Core.Storage;

/// <summary>Cartella scelta dall'utente da tenere indicizzata.</summary>
public sealed record RootFolder(long Id, string Path, DateTime? LastScanUtc)
{
    /// <summary>Cartella su un altro computer o su un NAS.</summary>
    public bool IsNetwork => NetworkPaths.IsUnc(Path);
}

/// <summary>Un file presente nell'indice.</summary>
public sealed record FileRecord(
    long Id,
    long RootId,
    string Path,
    string Name,
    string Extension,
    string Folder,
    long Size,
    DateTime ModifiedUtc,
    FileCategory Category,
    FileMeta? Meta = null)
{
    /// <summary>Etichette assegnate dall'utente, separate da virgola.</summary>
    public string? Tags { get; init; }

    /// <summary>Nota libera dell'utente.</summary>
    public string? Note { get; init; }

    /// <summary>Estratto del contenuto attorno al testo cercato (solo nella ricerca nel contenuto).</summary>
    public string? Snippet { get; init; }

    public string CategoryName => Category.DisplayName();
    public string? Version => Meta?.DisplayVersion;
    public string? Company => Meta?.CompanyName;
    public string? ProductName => Meta?.ProductName;
    public string? ArchitectureText => Meta?.ArchitectureText;
    public string? SignatureText => Meta?.SignatureText;
}

/// <summary>Numero di file e spazio occupato per categoria.</summary>
public sealed record CategoryStat(FileCategory Category, long Count, long TotalSize);

/// <summary>Parametri di ricerca: testo libero (anche vuoto) e categoria opzionale.</summary>
public sealed record SearchQuery(string? Text, FileCategory? Category = null, int Limit = 2000)
{
    /// <summary>Se valorizzato, limita la ricerca a queste estensioni (es. una sottocategoria).</summary>
    public IReadOnlyCollection<string>? Extensions { get; init; }

    /// <summary>Cerca anche nel testo dei documenti e nell'elenco dei file di ZIP e ISO.</summary>
    public bool InContent { get; init; }
}

/// <summary>Numero di file e spazio occupato per estensione.</summary>
public sealed record ExtensionStat(string Extension, long Count, long TotalSize);

/// <summary>Un gruppo di spostamenti eseguiti insieme, annullabile in blocco.</summary>
public sealed record MoveBatch(long Id, DateTime CreatedUtc, string Description, int ItemCount, bool Undone);

/// <summary>Singolo spostamento registrato nel log.</summary>
public sealed record MoveLogEntry(long Id, long BatchId, string FromPath, string ToPath);

/// <summary>File in attesa della lettura dei metadati.</summary>
public sealed record PendingMetadata(long Id, string Path, long Size, long ModifiedUnix);

/// <summary>Riepilogo dell'indice: numero di file, cartelle, spostamenti e spazio occupato dal database.</summary>
public sealed record IndexStats(long Files, long Roots, long MoveBatches, string DatabasePath, long DatabaseBytes);
