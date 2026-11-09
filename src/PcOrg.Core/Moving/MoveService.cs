using PcOrg.Core.Storage;

namespace PcOrg.Core.Moving;

/// <summary>Esito di un'operazione di spostamento o di annullamento.</summary>
public sealed record MoveResult(long BatchId, int Moved, IReadOnlyList<string> Errors);

/// <summary>
/// Esegue gli spostamenti approvati dall'utente, li registra nel log e permette di annullarli.
/// Non sovrascrive mai un file esistente.
/// </summary>
public sealed class MoveService(FileIndex index)
{
    public MoveResult Execute(IReadOnlyList<MoveItem> plan, string description,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var batchId = index.CreateMoveBatch(description);
        var errors = new List<string>();
        var moved = 0;

        foreach (var item in plan.Where(i => i.WillMove))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
                File.Move(item.Source, item.Destination, overwrite: false);
                index.LogMove(batchId, item.Source, item.Destination);
                index.ApplyMove(item.Source, item.Destination);
                moved++;
                progress?.Report(moved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{item.Source}: {ex.Message}");
            }
        }
        return new MoveResult(batchId, moved, errors);
    }

    /// <summary>Riporta i file di un gruppo di spostamenti nella posizione originale.</summary>
    public MoveResult Undo(long batchId)
    {
        var errors = new List<string>();
        var restored = 0;

        foreach (var entry in index.GetMoveLog(batchId).Reverse())
        {
            if (!File.Exists(entry.ToPath) && File.Exists(entry.FromPath))
                continue; // già ripristinato in un tentativo precedente
            if (!File.Exists(entry.ToPath))
            {
                errors.Add($"{entry.ToPath}: il file non è più lì.");
                continue;
            }
            if (File.Exists(entry.FromPath))
            {
                errors.Add($"{entry.FromPath}: nella posizione originale c'è già un altro file.");
                continue;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(entry.FromPath)!);
                File.Move(entry.ToPath, entry.FromPath, overwrite: false);
                index.ApplyMove(entry.ToPath, entry.FromPath);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{entry.ToPath}: {ex.Message}");
            }
        }

        if (errors.Count == 0)
            index.MarkBatchUndone(batchId);
        return new MoveResult(batchId, restored, errors);
    }
}
