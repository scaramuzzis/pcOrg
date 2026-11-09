using PcOrg.Core.Classification;

namespace PcOrg.Core.Storage;

/// <summary>Cartella scelta dall'utente da tenere indicizzata.</summary>
public sealed record RootFolder(long Id, string Path, DateTime? LastScanUtc);

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
    FileCategory Category)
{
    public string CategoryName => Category.DisplayName();
}

/// <summary>Numero di file e spazio occupato per categoria.</summary>
public sealed record CategoryStat(FileCategory Category, long Count, long TotalSize);

/// <summary>Parametri di ricerca: testo libero (anche vuoto) e categoria opzionale.</summary>
public sealed record SearchQuery(string? Text, FileCategory? Category = null, int Limit = 2000);

/// <summary>Un gruppo di spostamenti eseguiti insieme, annullabile in blocco.</summary>
public sealed record MoveBatch(long Id, DateTime CreatedUtc, string Description, int ItemCount, bool Undone);

/// <summary>Singolo spostamento registrato nel log.</summary>
public sealed record MoveLogEntry(long Id, long BatchId, string FromPath, string ToPath);
