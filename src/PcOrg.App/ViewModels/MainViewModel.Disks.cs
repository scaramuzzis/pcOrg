using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcOrg.App.Infrastructure;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Drives;
using PcOrg.Core.Storage;

namespace PcOrg.App.ViewModels;

/// <summary>Riga della scheda Dischi: spazio, file indicizzati, accessi di Gestione File e storico dei collegamenti.</summary>
public sealed record DiskRow(
    string Volume,
    string Name,
    string Type,
    string Format,
    long TotalBytes,
    long FreeBytes,
    long IndexedFiles,
    long IndexedBytes,
    int MonitoredFolders,
    DiskActivityStats Activity,
    DateTime? FirstSeenUtc,
    int Connections,
    bool IsConnected)
{
    public double UsedPercent => TotalBytes <= 0 ? 0 : 100.0 * (TotalBytes - FreeBytes) / TotalBytes;
    public string SpaceText => TotalBytes <= 0 ? "" : $"{FileSizeConverter.Format(FreeBytes)} liberi su {FileSizeConverter.Format(TotalBytes)}";
    public string IndexedText => IndexedFiles == 0 ? "—" : $"{IndexedFiles:N0} file · {FileSizeConverter.Format(IndexedBytes)}";
    public string ActivityText => Activity.Operations == 0 ? "Nessuna"
        : $"{Activity.DirectoriesRead:N0} cartelle, {Activity.FilesRead:N0} file";
    public string LatencyText => Activity.Operations == 0 ? "" : $"{Activity.AverageTime.TotalMilliseconds:N1} ms";
    public string StateText => IsConnected ? "Collegato" : "Non collegato";

    public override string ToString() => $"{Name}: {StateText}, {SpaceText}";
}

/// <summary>
/// Scheda "Dischi": per ogni disco (interni, USB, unità e cartelle di rete) mostra spazio, file
/// indicizzati e quante letture ha fatto Gestione File; si accorge dei dischi appena collegati.
/// </summary>
public sealed partial class MainViewModel
{
    public const int DisksTab = 5;

    public BulkObservableCollection<DiskRow> Disks { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MonitorDiskCommand))]
    private DiskRow? _selectedDisk;

    [ObservableProperty]
    private string _disksSummary = "";

    private DriveWatcher? _driveWatcher;

    /// <summary>Avvia il controllo dei dischi collegati (ogni 3 secondi, in background).</summary>
    public void StartDriveWatcher()
    {
        if (_driveWatcher is not null)
            return;
        Task.Run(() =>
        {
            try
            {
                var watcher = new DriveWatcher(TimeSpan.FromSeconds(3));
                watcher.Arrived += d => _dispatcher.InvokeAsync(() => OnDriveArrived(d));
                watcher.Removed += d => _dispatcher.InvokeAsync(() => OnDriveRemoved(d));
                _driveWatcher = watcher;
                // I dischi presenti all'avvio vengono registrati senza avvisi.
                var present = DriveCatalog.Read();
                _dispatcher.InvokeAsync(() =>
                {
                    foreach (var d in present)
                        Remember(d, connected: false);
                    SaveSettings();
                });
            }
            catch (Exception ex)
            {
                Log.Warn("Controllo dei dischi collegati non avviato.", ex);
            }
        });
    }

    /// <summary>Registra il disco nello storico; restituisce vero se non era mai stato visto.</summary>
    private bool Remember(DriveSnapshot drive, bool connected)
    {
        var now = DateTime.UtcNow;
        var known = _settings.KnownDisks.FirstOrDefault(k => k.Id == drive.Id);
        var isNew = known is null;
        if (known is null)
        {
            known = new KnownDisk { Id = drive.Id, FirstSeenUtc = now };
            _settings.KnownDisks.Add(known);
        }
        known.Label = drive.DisplayName;
        known.Type = drive.TypeText;
        known.TotalBytes = drive.TotalBytes;
        known.LastSeenUtc = now;
        if (connected)
            known.Connections++;
        return isNew;
    }

    private void OnDriveArrived(DriveSnapshot drive)
    {
        var isNew = Remember(drive, connected: true);
        SaveSettings();
        Log.Info($"Disco collegato: {drive.DisplayName}, {drive.TypeText}, {drive.Format}, " +
                 $"{FileSizeConverter.Format(drive.FreeBytes)} liberi su {FileSizeConverter.Format(drive.TotalBytes)}{(isNew ? " (mai visto prima)" : "")}.");
        Fire(RefreshDisksAsync(), "elenco dei dischi");
        if (!_settings.NotifyNewDisks)
            return;
        var monitored = Roots.Any(r => DiskActivity.VolumeOf(r.Path).Equals(drive.Name, StringComparison.OrdinalIgnoreCase));
        var text = $"{(isNew ? "Nuovo disco collegato" : "Disco collegato")}: {drive.DisplayName}, " +
                   $"{FileSizeConverter.Format(drive.UsedBytes)} usati su {FileSizeConverter.Format(drive.TotalBytes)}";
        if (monitored)
            ShowToast(text + ". Aggiorno i suoi file.", ToastKind.Info, "Dischi", () => { SelectedTabIndex = DisksTab; return Task.CompletedTask; });
        else
            ShowToast(text, ToastKind.Info, "Monitoralo", () => AddDiskAsRootAsync(drive.Name));
        if (monitored && !IsScanning && !IsBusy)
            Fire(ScanRootsAsync(Roots.Where(r => DiskActivity.VolumeOf(r.Path).Equals(drive.Name, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Root).ToList()), "aggiornamento del disco collegato");
    }

    private void OnDriveRemoved(DriveSnapshot drive)
    {
        Log.Info($"Disco scollegato: {drive.DisplayName}.");
        Fire(RefreshDisksAsync(), "elenco dei dischi");
    }

    /// <summary>Aggiunge l'intero disco alle cartelle monitorate e lo scansiona.</summary>
    private async Task AddDiskAsRootAsync(string driveRoot)
    {
        try
        {
            var root = await Task.Run(() => _index.AddRoot(driveRoot));
            ReloadRoots();
            await ScanOrQueueAsync(root);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _dialogs.Error(ex.Message, "Disco non aggiunto");
        }
    }

    [RelayCommand(CanExecute = nameof(CanMonitorDisk))]
    private Task MonitorDisk() => SelectedDisk is { } d ? AddDiskAsRootAsync(d.Volume) : Task.CompletedTask;

    private bool CanMonitorDisk() => SelectedDisk is { IsConnected: true, MonitoredFolders: 0 };

    [RelayCommand]
    private Task RefreshDisks() => RefreshDisksAsync();

    [RelayCommand]
    private async Task ResetDiskActivity()
    {
        DiskActivity.Reset();
        await RefreshDisksAsync();
    }

    /// <summary>Ricalcola la scheda Dischi: dischi collegati, cartelle di rete monitorate e dischi visti in passato.</summary>
    public async Task RefreshDisksAsync()
    {
        var roots = Roots.Select(r => r.Root).ToList();
        var known = _settings.KnownDisks.ToList();
        var rows = await Task.Run(() =>
        {
            var drives = DriveCatalog.Read();
            // File indicizzati per disco: somma delle categorie di ogni cartella monitorata che ci sta sopra.
            var perVolume = new Dictionary<string, (long Files, long Bytes, int Folders)>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                var volume = DiskActivity.VolumeOf(root.Path);
                var stats = _index.GetCategoryStats(root.Id);
                perVolume.TryGetValue(volume, out var t);
                perVolume[volume] = (t.Files + stats.Sum(s => s.Count), t.Bytes + stats.Sum(s => s.TotalSize), t.Folders + 1);
            }

            var list = new List<DiskRow>();
            foreach (var d in drives)
            {
                perVolume.TryGetValue(d.Name, out var t);
                var k = known.FirstOrDefault(x => x.Id == d.Id);
                list.Add(new DiskRow(d.Name, d.DisplayName, d.TypeText, d.Format, d.TotalBytes, d.FreeBytes, t.Files, t.Bytes, t.Folders,
                    DiskActivity.Get(d.Name), k?.FirstSeenUtc, k?.Connections ?? 0, IsConnected: true));
            }
            // Cartelle di rete (\\server\condivisione) e dischi monitorati ma non collegati ora.
            foreach (var (volume, t) in perVolume.Where(p => !drives.Any(d => d.Name.Equals(p.Key, StringComparison.OrdinalIgnoreCase))))
            {
                var isNetwork = Core.NetworkPaths.IsUnc(volume);
                var reachable = isNetwork && Directory.Exists(volume);
                list.Add(new DiskRow(volume, volume, isNetwork ? "Cartella di rete" : "Disco", "", 0, 0, t.Files, t.Bytes, t.Folders,
                    DiskActivity.Get(volume), null, 0, IsConnected: reachable));
            }
            // Dischi visti in passato e ora scollegati (chiavette, dischi esterni).
            foreach (var k in known.Where(k => !drives.Any(d => d.Id == k.Id)))
                list.Add(new DiskRow("", k.Label, k.Type, "", k.TotalBytes, 0, 0, 0, 0,
                    new DiskActivityStats("", 0, 0, TimeSpan.Zero, null), k.FirstSeenUtc, k.Connections, IsConnected: false));
            return list;
        });

        var selected = SelectedDisk?.Name;
        Disks.ReplaceAll(rows.OrderByDescending(r => r.IsConnected).ThenBy(r => r.Volume.Length == 0).ThenBy(r => r.Name));
        SelectedDisk = Disks.FirstOrDefault(d => d.Name == selected);
        var connected = rows.Count(r => r.IsConnected);
        var ops = rows.Sum(r => r.Activity.Operations);
        DisksSummary = $"{connected:N0} dischi collegati, {rows.Count - connected:N0} visti in passato. " +
                       (ops == 0 ? "Gestione File non ha ancora letto nulla da quando è aperto."
                           : $"Da quando è aperto Gestione File ha fatto {ops:N0} letture.");
    }
}
