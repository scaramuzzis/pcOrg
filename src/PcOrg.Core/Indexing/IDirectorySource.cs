namespace PcOrg.Core.Indexing;

/// <summary>
/// Da dove la scansione legge cartelle e file. Di norma il file system; per la scansione veloce NTFS una
/// fotografia della Master File Table letta in un colpo solo (<see cref="Ntfs.MftSnapshot"/>).
/// </summary>
public interface IDirectorySource
{
    /// <summary>Data di modifica della cartella in millisecondi Unix, o null se la cartella non esiste.</summary>
    long? GetDirectoryTime(string dir, CancellationToken cancellationToken);

    /// <summary>File e sottocartelle (esclusi file di sistema e collegamenti a cartelle).</summary>
    (IReadOnlyList<ScannedFile> Files, IReadOnlyList<string> Subdirectories) List(string dir, CancellationToken cancellationToken);
}
