using System.IO.Hashing;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Duplicates;

/// <summary>Gruppo di file con contenuto identico (stessa impronta completa).</summary>
public sealed record DuplicateGroup(string Hash, long Size, IReadOnlyList<FileRecord> Files)
{
    /// <summary>Spazio che si libererebbe tenendo una sola copia.</summary>
    public long WastedBytes => Size * (Files.Count - 1);

    public string Name => Files.Count > 0 ? Files[0].Name : "";
}

/// <summary>Avanzamento della ricerca dei cloni.</summary>
public readonly record struct DuplicateProgress(string Phase, int Done, int Total);

/// <summary>Opzioni della ricerca dei cloni.</summary>
/// <param name="RootId">Se valorizzato, cerca i cloni solo dentro quella cartella monitorata (anche se di rete).</param>
public sealed record DuplicateOptions(long MinSize = 1, bool IncludeNetwork = false, long? RootId = null);

/// <summary>
/// Trova i file con contenuto identico, anche se hanno nomi diversi o stanno in cartelle diverse.
/// </summary>
/// <remarks>
/// Il confronto avviene a tre stadi, dal più economico al più costoso, per leggere il meno possibile:
/// <list type="number">
/// <item><b>Dimensione</b>: due file possono essere identici solo se hanno la stessa dimensione (query sull'indice).</item>
/// <item><b>Impronta veloce</b>: XxHash64 dei primi e degli ultimi 64 KB, solo per i file con dimensione in comune.</item>
/// <item><b>Impronta completa</b>: XxHash128 dell'intero file, solo per chi coincide anche nell'impronta veloce.</item>
/// </list>
/// Le impronte vengono salvate nell'indice: alla ricerca successiva si rileggono solo i file nuovi o cambiati.
/// XxHash è un algoritmo non crittografico molto veloce; con 128 bit la probabilità di due file diversi con la
/// stessa impronta è trascurabile.
/// </remarks>
public static class DuplicateFinder
{
    private const int EdgeBytes = 64 * 1024;

    /// <summary>Impronte effettivamente calcolate (lette da disco) nell'ultima ricerca: le altre erano in memoria.</summary>
    public static int LastComputed { get; private set; }

    private static int _computed;

    public static IReadOnlyList<DuplicateGroup> Find(FileIndex index, DuplicateOptions options,
        IProgress<DuplicateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        LastComputed = 0;
        _computed = 0;
        var candidates = index.GetDuplicateCandidates(options.MinSize, root => options.RootId is { } only ? root.Id == only : options.IncludeNetwork || !root.IsNetwork);
        Log.Info($"Ricerca cloni: {candidates.Count:N0} file con dimensione in comune.");

        // Stadio 2: impronta veloce dei candidati (quelle già note si riusano).
        var withPartial = ComputeHashes(index, candidates, full: false, "Impronta veloce", progress, cancellationToken);

        // Stadio 3: impronta completa solo per chi coincide in dimensione e impronta veloce.
        var needFull = withPartial
            .Where(c => c.PartialHash is not null)
            .GroupBy(c => (c.Size, c.PartialHash))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToList();
        var withFull = ComputeHashes(index, needFull, full: true, "Confronto completo", progress, cancellationToken);

        var groups = withFull
            .Where(c => c.FullHash is not null)
            .GroupBy(c => (c.Size, c.FullHash))
            .Where(g => g.Count() > 1)
            .ToList();

        var records = index.GetFiles(groups.SelectMany(g => g.Select(c => c.Id)).ToList()).ToDictionary(f => f.Id);
        var result = groups
            .Select(g => new DuplicateGroup(g.Key.FullHash!, g.Key.Size,
                g.Select(c => records.GetValueOrDefault(c.Id)).OfType<FileRecord>()
                 .OrderBy(f => f.ModifiedUtc).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList()))
            .Where(g => g.Files.Count > 1)
            .OrderByDescending(g => g.WastedBytes)
            .ToList();
        Log.Info($"Ricerca cloni completata: {result.Count:N0} gruppi, {result.Sum(g => g.WastedBytes):N0} byte recuperabili.");
        return result;
    }

    /// <summary>Calcola le impronte mancanti (in parallelo) e le salva a blocchi.</summary>
    private static List<HashCandidate> ComputeHashes(FileIndex index, IReadOnlyList<HashCandidate> items, bool full,
        string phase, IProgress<DuplicateProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new HashCandidate[items.Count];
        var toSave = new System.Collections.Concurrent.ConcurrentBag<HashCandidate>();
        var done = 0;
        progress?.Report(new DuplicateProgress(phase, 0, items.Count));

        Parallel.For(0, items.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), CancellationToken = cancellationToken },
            i =>
            {
                var item = items[i];
                var known = full ? item.FullHash : item.PartialHash;
                if (known is null)
                {
                    var hash = TryHash(item.Path, item.Size, full, cancellationToken);
                    Interlocked.Increment(ref _computed);
                    item = full ? item with { FullHash = hash } : item with { PartialHash = hash };
                    if (hash is not null)
                        toSave.Add(item);
                }
                result[i] = item;
                var n = Interlocked.Increment(ref done);
                if (n % 200 == 0)
                    progress?.Report(new DuplicateProgress(phase, n, items.Count));
            });

        index.SaveHashes(toSave);
        LastComputed = _computed;
        progress?.Report(new DuplicateProgress(phase, items.Count, items.Count));
        return [.. result];
    }

    private static string? TryHash(string path, long expectedSize, bool full, CancellationToken cancellationToken)
    {
        try
        {
            if (CloudFiles.IsOnlineOnly(path))
                return null; // non si scaricano i file solo online per confrontarli
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1 << 20, FileOptions.SequentialScan);
            if (stream.Length != expectedSize)
                return null; // cambiato dall'ultima scansione: verrà riconsiderato al prossimo aggiornamento
            return full ? FullHash(stream, cancellationToken) : PartialHash(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Primi e ultimi 64 KB più la dimensione: distingue quasi tutti i file diversi leggendo pochissimo.</summary>
    internal static string PartialHash(Stream stream)
    {
        var hash = new XxHash64();
        var buffer = new byte[EdgeBytes];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        hash.Append(buffer.AsSpan(0, read));
        if (stream.Length > EdgeBytes * 2)
        {
            stream.Seek(-EdgeBytes, SeekOrigin.End);
            read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            hash.Append(buffer.AsSpan(0, read));
        }
        hash.Append(BitConverter.GetBytes(stream.Length));
        return Convert.ToHexString(hash.GetCurrentHash());
    }

    internal static string FullHash(Stream stream, CancellationToken cancellationToken)
    {
        var hash = new XxHash128();
        var buffer = new byte[1 << 20];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.Append(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(hash.GetCurrentHash());
    }
}
