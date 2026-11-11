using System.Collections.Concurrent;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Indexing;

/// <summary>
/// Osserva le cartelle monitorate mentre l'app è aperta e segnala i percorsi cambiati
/// (file aggiunti, modificati, rinominati o eliminati), senza bisogno di scansioni.
/// </summary>
/// <remarks>
/// Usa il FileSystemWatcher di Windows. Gli eventi arrivano a raffiche (una copia di 1.000 file ne
/// genera migliaia): vengono raccolti e consegnati tutti insieme con <see cref="ChangesReady"/> dopo
/// <see cref="Quiet"/> di silenzio. Se Windows perde eventi (buffer pieno) viene segnalata l'intera
/// cartella monitorata, che verrà aggiornata con l'aggiornamento rapido.
/// </remarks>
public sealed class FolderWatcher : IDisposable
{
    /// <summary>Tempo di quiete dopo l'ultimo evento prima di consegnare le modifiche.</summary>
    public static TimeSpan Quiet { get; set; } = TimeSpan.FromSeconds(1.5);

    private readonly ConcurrentDictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, FileSystemWatcher> _watchers = [];
    private readonly Timer _timer;
    private readonly Func<string, bool> _ignore;
    private bool _disposed;

    /// <summary>Modifiche raccolte (cartella monitorata, percorso). Viene chiamato su un thread in background.</summary>
    public event Action<IReadOnlyList<(long RootId, string Path)>>? ChangesReady;

    /// <param name="ignore">Percorsi da non segnalare (es. i file dei dati di pcOrg stesso).</param>
    public FolderWatcher(Func<string, bool>? ignore = null)
    {
        _ignore = ignore ?? (_ => false);
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Cartelle attualmente osservate.</summary>
    public IReadOnlyCollection<long> Watched => _watchers.Keys;

    /// <summary>Allinea gli osservatori all'elenco delle cartelle monitorate (aggiunge i nuovi, chiude i vecchi).</summary>
    /// <remarks>
    /// Può essere lento: per una cartella di rete non raggiungibile Windows aspetta a lungo prima di
    /// rispondere. Va quindi chiamato in background (vedi <see cref="WatchInBackground"/>), mai dal
    /// thread dell'interfaccia.
    /// </remarks>
    public void Watch(IEnumerable<RootFolder> roots)
    {
        var wanted = roots.ToDictionary(r => r.Id);
        lock (_gate)
            WatchCore(wanted);
    }

    /// <summary>Come <see cref="Watch"/>, ma senza far attendere chi chiama.</summary>
    public Task WatchInBackground(IReadOnlyList<RootFolder> roots) => Task.Run(() =>
    {
        try
        {
            Watch(roots);
        }
        catch (Exception ex)
        {
            Log.Warn("Attivazione dell'osservazione delle cartelle non riuscita.", ex);
        }
    });

    private readonly object _gate = new();

    private void WatchCore(Dictionary<long, RootFolder> wanted)
    {
        if (_disposed)
            return;
        foreach (var id in _watchers.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            _watchers[id].Dispose();
            _watchers.Remove(id);
        }
        foreach (var root in wanted.Values.Where(r => !_watchers.ContainsKey(r.Id)))
        {
            try
            {
                var watcher = new FileSystemWatcher(root.Path)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                var id = root.Id;
                watcher.Created += (_, e) => Enqueue(id, e.FullPath);
                watcher.Changed += (_, e) => Enqueue(id, e.FullPath);
                watcher.Deleted += (_, e) => Enqueue(id, e.FullPath);
                watcher.Renamed += (_, e) =>
                {
                    Enqueue(id, e.OldFullPath);
                    Enqueue(id, e.FullPath);
                };
                watcher.Error += (_, e) =>
                {
                    Log.Warn($"Osservatore di {root.Path}: eventi persi, la cartella verrà ricontrollata.", e.GetException());
                    Enqueue(id, root.Path);
                };
                watcher.EnableRaisingEvents = true;
                _watchers[id] = watcher;
                Log.Info($"Osservazione attiva su {root.Path}");
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Es. cartella di rete non raggiungibile o che non supporta le notifiche.
                Log.Warn($"Impossibile osservare {root.Path}: verrà aggiornata solo con l'aggiornamento rapido.", ex);
            }
        }
    }

    /// <summary>Smette di osservare tutte le cartelle.</summary>
    public void StopAll() => Watch([]);

    private void Enqueue(long rootId, string path)
    {
        if (_ignore(path))
            return;
        _pending[path] = rootId;
        _timer.Change(Quiet, Timeout.InfiniteTimeSpan); // si riparte da zero a ogni evento
    }

    /// <summary>Consegna le modifiche raccolte (pubblico per i test).</summary>
    public void Flush()
    {
        if (_pending.IsEmpty)
            return;
        var batch = new List<(long, string)>();
        foreach (var key in _pending.Keys.ToList())
            if (_pending.TryRemove(key, out var rootId))
                batch.Add((rootId, key));
        if (batch.Count > 0)
            ChangesReady?.Invoke(batch);
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_gate)
        {
            foreach (var watcher in _watchers.Values)
                watcher.Dispose();
            _watchers.Clear();
        }
        _timer.Dispose();
    }
}
