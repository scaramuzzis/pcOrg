using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcOrg.App.Infrastructure;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Drivers;
using PcOrg.Core.Metadata;
using PcOrg.Core.Storage;

namespace PcOrg.App.ViewModels;

/// <summary>Una periferica presente con i driver dell'archivio che la riguardano.</summary>
public sealed record DeviceRow(Device Device, IReadOnlyList<DriverFileRow> Drivers)
{
    public string Name => Device.Name;
    public string? Class => Device.Class;
    public string? InstalledVersion => Device.DriverVersion;
    public string? Provider => Device.DriverProvider;
    public int DriverCount => Drivers.Count;

    /// <summary>La versione più recente presente nell'archivio.</summary>
    public string? BestVersion => Drivers.Select(d => d.Version).OrderByDescending(v => v, VersionComparer.Instance).FirstOrDefault();

    public bool HasUpdate => Drivers.Any(d => d.State == DriverState.PiuRecente);

    public string State =>
        Drivers.Count == 0 ? "Nessun driver nell'archivio"
        : HasUpdate ? $"Aggiornamento disponibile: {BestVersion}"
        : Drivers.Any(d => d.State == DriverState.StessaVersione) ? "Hai il driver installato"
        : Drivers.All(d => d.State == DriverState.Superato) ? "Solo versioni più vecchie"
        : "Driver compatibili";
}

/// <summary>Un pacchetto driver dell'archivio, con lo stato rispetto al driver installato.</summary>
public sealed record DriverFileRow(FileRecord File, DriverState State, string? InstalledVersion)
{
    public string? Version => File.Version;

    public string StateText => State switch
    {
        DriverState.PiuRecente => "Più recente dell'installato",
        DriverState.StessaVersione => "È quello installato",
        DriverState.Superato => "Più vecchio dell'installato",
        DriverState.NonServe => "Non serve a questo PC",
        _ => "Compatibile",
    };
}

/// <summary>Scheda "Driver e periferiche": quali driver dell'archivio servono a questo PC e quali no.</summary>
public sealed partial class MainViewModel
{
    private List<DeviceRow> _allDevices = [];

    public BulkObservableCollection<DeviceRow> DeviceRows { get; } = [];
    public ObservableCollection<DriverFileRow> DriverFiles { get; } = [];

    [ObservableProperty]
    private DeviceRow? _selectedDevice;

    /// <summary>Mostra solo le periferiche per cui l'archivio contiene almeno un driver.</summary>
    [ObservableProperty]
    private bool _onlyDevicesWithDrivers = true;

    /// <summary>In basso, invece dei driver della periferica, mostra quelli che non servono a nessuna periferica.</summary>
    [ObservableProperty]
    private bool _showUnneededDrivers;

    [ObservableProperty]
    private string _driversSummary = "Lettura delle periferiche…";

    /// <summary>Pacchetti driver che non servono a nessuna periferica presente.</summary>
    private List<DriverFileRow> _unneeded = [];

    partial void OnSelectedDeviceChanged(DeviceRow? value) => ShowDriverFiles();
    partial void OnShowUnneededDriversChanged(bool value) => ShowDriverFiles();
    partial void OnOnlyDevicesWithDriversChanged(bool value) => ShowDevices();

    /// <summary>
    /// Legge periferiche presenti e pacchetti driver dell'archivio e li confronta. Viene chiamato con
    /// l'aggiornamento dei programmi installati (avvio e dopo ogni scansione).
    /// </summary>
    private async Task RefreshDriversAsync()
    {
        var (devices, packages) = await Task.Run(() => (DeviceInventory.Read(), _index.GetDriverPackages()));
        var matcher = new DriverMatcher(devices);
        var matches = new Dictionary<long, DriverMatch>();
        var perDevice = devices.ToDictionary(d => d, _ => new List<DriverFileRow>());
        var unneeded = new List<DriverFileRow>();
        foreach (var (file, ids) in packages)
        {
            var match = matcher.Match(ids, file.Version);
            matches[file.Id] = match;
            if (match.Devices.Count == 0)
            {
                unneeded.Add(new DriverFileRow(file, DriverState.NonServe, null));
                continue;
            }
            foreach (var device in match.Devices)
                perDevice[device].Add(new DriverFileRow(file, StateFor(file.Version, device.DriverVersion), device.DriverVersion));
        }

        InstalledCatalog.Devices = matcher;
        InstalledCatalog.Drivers = matches;
        // Per cartella si tiene l'esito più utile: prima un driver che serve, poi uno che non serve.
        InstalledCatalog.DriverFolders = packages
            .GroupBy(p => p.File.Folder, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(p => matches[p.File.Id]).OrderBy(m => m.State == DriverState.NonServe).First(),
                StringComparer.OrdinalIgnoreCase);

        _allDevices = perDevice
            .Select(kv => new DeviceRow(kv.Key, kv.Value.OrderByDescending(r => r.Version, VersionComparer.Instance).ToList()))
            .OrderByDescending(r => r.HasUpdate).ThenByDescending(r => r.DriverCount > 0).ThenBy(r => r.Class).ThenBy(r => r.Name)
            .ToList();
        _unneeded = unneeded.OrderBy(r => r.File.Folder).ThenBy(r => r.File.Name).ToList();

        var useful = packages.Count - unneeded.Count;
        var updates = _allDevices.Count(d => d.HasUpdate);
        DriversSummary = devices.Count == 0
            ? $"Periferiche non disponibili su questo sistema. Pacchetti driver nell'archivio: {packages.Count:N0}."
            : $"{devices.Count:N0} periferiche presenti. Nell'archivio ci sono {packages.Count:N0} pacchetti driver (.inf): " +
              $"{useful:N0} servono a questo PC, {unneeded.Count:N0} no. " +
              (updates > 0 ? $"Per {updates:N0} periferiche c'è un driver più recente di quello installato." : "Nessun aggiornamento disponibile.");
        Log.Info($"Driver: {devices.Count} periferiche, {packages.Count} pacchetti, {useful} utili, {updates} aggiornamenti disponibili.");
        ShowDevices();
        UpdateDashboard();
    }

    private static DriverState StateFor(string? archive, string? installed)
    {
        if (archive is null || installed is null)
            return DriverState.Compatibile;
        var cmp = VersionComparer.Instance.Compare(archive, installed);
        return cmp == 0 ? DriverState.StessaVersione : cmp > 0 ? DriverState.PiuRecente : DriverState.Superato;
    }

    private void ShowDevices()
    {
        var selected = SelectedDevice?.Device;
        DeviceRows.ReplaceAll(_allDevices.Where(r => !OnlyDevicesWithDrivers || r.DriverCount > 0));
        SelectedDevice = DeviceRows.FirstOrDefault(r => r.Device == selected) ?? DeviceRows.FirstOrDefault();
        ShowDriverFiles();
    }

    private void ShowDriverFiles()
    {
        DriverFiles.Clear();
        var rows = ShowUnneededDrivers ? _unneeded : SelectedDevice?.Drivers ?? [];
        foreach (var row in rows)
            DriverFiles.Add(row);
    }

    /// <summary>Seleziona tutti i driver che non servono a questo PC (per archiviarli o cestinarli).</summary>
    [RelayCommand]
    private void SelectUnneededDrivers()
    {
        ShowUnneededDrivers = true;
        SelectedFiles = _unneeded.Select(r => r.File).ToList();
        ShowToast(_unneeded.Count == 0 ? "Tutti i driver dell'archivio servono a questo PC"
            : $"Selezionati {_unneeded.Count:N0} pacchetti driver che non servono a questo PC: usa Sposta in… o Sposta nel Cestino",
            ToastKind.Info);
    }
}
