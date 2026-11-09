using PcOrg.Core.Storage;

namespace PcOrg.Core.Metadata;

/// <summary>Legge in background i metadati dei file nuovi o modificati e li salva nell'indice.</summary>
public static class MetadataUpdater
{
    /// <param name="progress">Riceve (letti, totale).</param>
    /// <returns>Numero di file letti.</returns>
    public static int Run(FileIndex index, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default, bool verifySignatures = true)
    {
        var total = index.CountFilesNeedingMetadata();
        var done = 0;
        progress?.Report((0, total));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = index.GetFilesNeedingMetadata(200);
            if (batch.Count == 0)
                break;

            var results = new (PendingMetadata File, FileMeta? Meta)[batch.Count];
            Parallel.For(0, batch.Count,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), CancellationToken = cancellationToken },
                i =>
                {
                    FileMeta? meta = null;
                    try
                    {
                        meta = MetadataExtractor.Extract(batch[i].Path, verifySignatures);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // File illeggibile o malformato: si salva senza metadati, così non blocca
                        // la lettura degli altri e non viene riletto finché non cambia.
                    }
                    results[i] = (batch[i], meta);
                });

            index.SaveMetadata(results);
            done += batch.Count;
            progress?.Report((done, Math.Max(total, done)));
        }
        return done;
    }
}
