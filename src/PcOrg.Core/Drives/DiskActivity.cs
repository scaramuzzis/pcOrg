using System.Collections.Concurrent;
using System.Diagnostics;

namespace PcOrg.Core.Drives;

/// <summary>Letture fatte da Gestione File su un disco (o una condivisione di rete) da quando è aperto.</summary>
public sealed record DiskActivityStats(string Volume, long DirectoriesRead, long FilesRead, TimeSpan TotalTime, DateTime? LastAccessUtc)
{
    public long Operations => DirectoriesRead + FilesRead;

    /// <summary>Tempo medio di una lettura: indica quanto il disco o la rete sono lenti a rispondere.</summary>
    public TimeSpan AverageTime => Operations == 0 ? TimeSpan.Zero : TotalTime / Operations;
}

/// <summary>
/// Contatori degli accessi ai dischi fatti dall'app (elenco delle cartelle, lettura dei file per versioni,
/// firme e contenuto). Servono alla scheda Dischi per vedere quanto e dove Gestione File sta lavorando.
/// Sicuro da usare da più thread.
/// </summary>
public static class DiskActivity
{
    private sealed class Counter
    {
        public long Directories;
        public long Files;
        public long Ticks;
        public long LastAccessTicks;
    }

    private static readonly ConcurrentDictionary<string, Counter> Counters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Disco a cui appartiene un percorso: "C:\" per i dischi locali, "\\server\condivisione" per la rete.
    /// </summary>
    public static string VolumeOf(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "";
        if (NetworkPaths.IsUnc(path))
        {
            var parts = path.TrimStart('\\', '/').Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : path;
        }
        if (path.Length >= 2 && path[1] == ':')
            return char.ToUpperInvariant(path[0]) + @":\";
        return Path.GetPathRoot(path) ?? path;
    }

    public static void RecordDirectory(string path, TimeSpan elapsed) => Record(path, elapsed, directory: true);

    public static void RecordFile(string path, TimeSpan elapsed) => Record(path, elapsed, directory: false);

    /// <summary>Misura una lettura e la registra.</summary>
    public static T Measure<T>(string path, bool directory, Func<T> read)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            return read();
        }
        finally
        {
            Record(path, Stopwatch.GetElapsedTime(start), directory);
        }
    }

    private static void Record(string path, TimeSpan elapsed, bool directory)
    {
        var counter = Counters.GetOrAdd(VolumeOf(path), _ => new Counter());
        if (directory)
            Interlocked.Increment(ref counter.Directories);
        else
            Interlocked.Increment(ref counter.Files);
        Interlocked.Add(ref counter.Ticks, elapsed.Ticks);
        Interlocked.Exchange(ref counter.LastAccessTicks, DateTime.UtcNow.Ticks);
    }

    public static DiskActivityStats Get(string volume)
    {
        if (!Counters.TryGetValue(volume, out var c))
            return new DiskActivityStats(volume, 0, 0, TimeSpan.Zero, null);
        var last = Interlocked.Read(ref c.LastAccessTicks);
        return new DiskActivityStats(volume, Interlocked.Read(ref c.Directories), Interlocked.Read(ref c.Files),
            TimeSpan.FromTicks(Interlocked.Read(ref c.Ticks)), last == 0 ? null : new DateTime(last, DateTimeKind.Utc));
    }

    public static IReadOnlyList<DiskActivityStats> All() => Counters.Keys.Select(Get).ToList();

    /// <summary>Azzera i contatori (pulsante "Azzera" della scheda Dischi).</summary>
    public static void Reset() => Counters.Clear();
}
