using System.IO;
using PcOrg.App.Infrastructure;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Indexing;
using PcOrg.Core.Metadata;
using PcOrg.Core.Storage;

namespace PcOrg.App.ViewModels;

/// <summary>Rilevamento automatico delle modifiche nelle cartelle mentre l'app è aperta.</summary>
public sealed partial class MainViewModel
{
    private FolderWatcher? _watcher;

    /// <summary>Applica una raffica di modifiche alla volta, nell'ordine in cui arrivano.</summary>
    private readonly SemaphoreSlim _watchGate = new(1, 1);

    /// <summary>Attiva o disattiva l'osservazione delle cartelle monitorate.</summary>
    public void EnableWatching(bool enabled)
    {
        if (!enabled)
        {
            _watcher?.Dispose();
            _watcher = null;
            return;
        }
        if (_watcher is not null)
            return;
        // Non si osservano i file di pcOrg stesso (indice, registro), se stanno in una cartella monitorata.
        var dataFolder = AppPaths.DataFolder;
        _watcher = new FolderWatcher(path => Core.PathUtil.IsSameOrUnder(path, dataFolder));
        _watcher.ChangesReady += OnChangesReady;
        _watcher.Watch(_index.GetRoots());
    }

    /// <summary>Chiamato in background quando l'osservatore ha raccolto delle modifiche.</summary>
    private async void OnChangesReady(IReadOnlyList<(long RootId, string Path)> changes)
    {
        // Durante una scansione o uno spostamento l'indice è già impegnato in scrittura: si aspetta,
        // così le modifiche rilevate non vanno perse per un blocco del database.
        while (IsBusy)
            await Task.Delay(500);
        await _watchGate.WaitAsync();
        try
        {
            var result = await Task.Run(() => _index.ApplyChanges(changes));
            if (result.FilesChanged == 0)
                return;
            Log.Info($"Modifiche rilevate automaticamente: {DescribeChanges(result)} " +
                     $"({string.Join(", ", changes.Take(5).Select(c => Path.GetFileName(c.Path)))}{(changes.Count > 5 ? ", …" : "")}).");

            // Versioni e firme dei programmi appena arrivati.
            if (result.FilesAdded + result.FilesUpdated > 0)
            {
                await Task.Run(() => MetadataUpdater.Run(_index, verifySignatures: _settings.VerifySignatures));
                if (_settings.IndexContent)
                    await Task.Run(() => PcOrg.Core.Content.ContentIndexer.Run(_index));
            }

            await _dispatcher.InvokeAsync(async () =>
            {
                if (!IsBusy)
                    Status = $"Modifiche rilevate: {DescribeChanges(result)}";
                await RefreshAsync();
            }).Task.Unwrap();
        }
        catch (Exception ex)
        {
            Log.Error("Errore nell'applicare le modifiche rilevate.", ex);
        }
        finally
        {
            _watchGate.Release();
        }
    }

    /// <summary>Da chiamare alla chiusura: smette di osservare le cartelle.</summary>
    public void Shutdown() => EnableWatching(false);
}
