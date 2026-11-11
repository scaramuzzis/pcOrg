using PcOrg.Core.Storage;

namespace PcOrg.Core.Content;

/// <summary>Indicizza in background il contenuto dei documenti e degli archivi nuovi o modificati.</summary>
public static class ContentIndexer
{
    /// <param name="progress">Riceve (elaborati, totale).</param>
    /// <returns>Numero di file elaborati.</returns>
    public static int Run(FileIndex index, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var total = index.CountFilesNeedingContent();
        var done = 0;
        progress?.Report((0, total));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = index.GetFilesNeedingContent(20);
            if (batch.Count == 0)
                break;
            var texts = new string?[batch.Count];
            // Un file alla volta: l'estrazione di un PDF grande può occupare centinaia di MB, e due
            // insieme raddoppiavano il picco di memoria.
            for (var i = 0; i < batch.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                texts[i] = ContentExtractor.Extract(batch[i].Path);
            }
            // Si salva anche chi non ha testo, così non viene riletto finché non cambia.
            index.SaveContent(batch.Select((file, i) => (file, texts[i])));
            done += batch.Count;
            progress?.Report((done, Math.Max(total, done)));
            GC.Collect(); // i documenti appena letti non servono più: non si lascia accumulare la memoria
        }
        return done;
    }
}
