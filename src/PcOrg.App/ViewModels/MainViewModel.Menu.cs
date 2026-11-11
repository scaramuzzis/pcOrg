using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcOrg.App.Infrastructure;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Storage;

namespace PcOrg.App.ViewModels;

/// <summary>Comandi e opzioni della barra dei menu.</summary>
public sealed partial class MainViewModel
{
    private AppSettings _settings = new();
    private bool _persistSettings;
    private bool _scanOnStartup;

    /// <summary>Preferenze in uso (colonne, finestra, opzioni). Salvate con <see cref="SaveSettings"/>.</summary>
    public AppSettings Settings => _settings;

    /// <summary>
    /// Applica le preferenze salvate (tema, pannelli, opzioni). Le modifiche successive vengono salvate.
    /// Va chiamato subito dopo il costruttore, prima che la finestra venga mostrata.
    /// </summary>
    public void UseSettings(AppSettings settings)
    {
        _settings = settings;
        _persistSettings = settings.FilePath is not null;
        _scanOnStartup = settings.ScanOnStartup;
        ShowPreviewPanel = settings.ShowPreviewPanel;
        ShowDetailsPanel = settings.ShowDetailsPanel;
        SearchInContent = settings.SearchInContent;
        ApplyTheme(settings.Theme, save: false);
        Log.Configure(settings.LogEnabled, settings.LogLevel);
        EnableWatching(settings.WatchFolders);
    }

    /// <summary>Salva le preferenze su disco (se l'app le ha caricate da file).</summary>
    public void SaveSettings()
    {
        if (_persistSettings)
            _settings.Save();
    }

    /// <summary>Opzioni modificabili dalla finestra Impostazioni.</summary>
    public sealed record Options(string Theme, bool ScanOnStartup, bool WatchFolders, bool VerifySignatures,
        bool ShowPreview, bool ShowDetails, bool LogEnabled, LogLevel LogLevel,
        bool IndexContent = true, bool CheckUpdatesOnStartup = false);

    /// <summary>Applica e salva le opzioni scelte nella finestra Impostazioni.</summary>
    public void ApplyOptions(Options o)
    {
        _settings.ScanOnStartup = o.ScanOnStartup;
        _settings.WatchFolders = o.WatchFolders;
        _settings.VerifySignatures = o.VerifySignatures;
        _settings.LogEnabled = o.LogEnabled;
        _settings.LogLevel = o.LogLevel;
        _settings.IndexContent = o.IndexContent;
        _settings.CheckUpdatesOnStartup = o.CheckUpdatesOnStartup;
        ShowPreviewPanel = o.ShowPreview;
        ShowDetailsPanel = o.ShowDetails;
        ApplyTheme(o.Theme, save: false);
        Log.Configure(o.LogEnabled, o.LogLevel);
        EnableWatching(o.WatchFolders);
        SaveSettings();
        Log.Info($"Impostazioni: tema {o.Theme}, aggiornamento all'avvio {o.ScanOnStartup}, rilevamento automatico " +
                 $"{o.WatchFolders}, verifica firme {o.VerifySignatures}, registro {o.LogEnabled} ({o.LogLevel}).");
    }

    // ------------------------------------------------------------------ Manutenzione dell'indice

    public IndexStats GetIndexStats() => _index.GetStats();

    /// <summary>Compatta il file dell'indice.</summary>
    public async Task CompactIndexAsync()
    {
        IsBusy = true;
        try
        {
            Status = "Compattazione dell'indice…";
            var before = _index.GetStats().DatabaseBytes;
            await Task.Run(_index.Compact);
            var after = _index.GetStats().DatabaseBytes;
            Log.Info($"Indice compattato: da {FileSizeConverter.Format(before)} a {FileSizeConverter.Format(after)}.");
            Status = "Pronto";
            ShowToast($"Indice compattato: {FileSizeConverter.Format(before)} → {FileSizeConverter.Format(after)}", ToastKind.Info);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Svuota l'elenco dei file e lo ricostruisce con una nuova scansione.</summary>
    public async Task RebuildIndexAsync()
    {
        Log.Info("Ricostruzione dell'indice richiesta dall'utente.");
        await Task.Run(_index.ClearFiles);
        ReloadRoots();
        await RefreshAsync();
        await ScanRootsAsync(Roots.Select(r => r.Root).ToList());
    }

    [RelayCommand]
    private void OpenLog()
    {
        var path = Log.FilePath;
        if (path is null || !File.Exists(path))
        {
            ShowToast("Il registro attività è ancora vuoto", ToastKind.Info);
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ Visualizza

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPanelVisible))]
    private bool _showPreviewPanel = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsPanelVisible))]
    private bool _showDetailsPanel = true;

    /// <summary>Scheda attiva: 0 = Panoramica, 1 = File, 2 = Prodotti, 3 = Cloni, 4 = Driver.</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    public bool PreviewPanelVisible => IsPreviewVisible && ShowPreviewPanel;
    public bool DetailsPanelVisible => SelectedFile is not null && ShowDetailsPanel;

    partial void OnShowPreviewPanelChanged(bool value)
    {
        _settings.ShowPreviewPanel = value;
        SaveSettings();
    }

    partial void OnShowDetailsPanelChanged(bool value)
    {
        _settings.ShowDetailsPanel = value;
        SaveSettings();
    }

    [RelayCommand]
    private void ShowTab(string? index) => SelectedTabIndex = int.TryParse(index, out var i) ? i : 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark))]
    private string _currentTheme = "Sistema";

    public bool IsThemeSystem => CurrentTheme == "Sistema";
    public bool IsThemeLight => CurrentTheme == "Chiaro";
    public bool IsThemeDark => CurrentTheme == "Scuro";

    [RelayCommand]
    private void SetTheme(string? theme) => ApplyTheme(theme ?? "Sistema", save: true);

    private void ApplyTheme(string theme, bool save)
    {
        CurrentTheme = theme is "Chiaro" or "Scuro" ? theme : "Sistema";
        if (Application.Current is { } app)
            app.ThemeMode = AppSettings.ToThemeMode(CurrentTheme);
        _settings.Theme = CurrentTheme;
        if (save)
            SaveSettings();
    }

    // ------------------------------------------------------------------ File

    /// <summary>Esporta in CSV (apribile con Excel) tutti i file della vista attuale: categoria e ricerca.</summary>
    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var path = _dialogs.PickSaveFile("Esporta l'elenco dei file",
            $"Gestione File {DateTime.Now:yyyy-MM-dd}.csv", "File CSV per Excel (*.csv)|*.csv");
        if (path is null)
            return;

        var query = new SearchQuery(SearchText, SelectedCategory?.Category, Limit: 1_000_000)
        {
            InContent = SearchInContent,
            Extensions = SelectedCategory?.Extensions,
        };
        try
        {
            var count = await Task.Run(() =>
            {
                var files = _index.Search(query);
                CsvExporter.Write(files, path);
                return files.Count;
            });
            ShowToast($"Esportati {count:N0} file in {Path.GetFileName(path)}", ToastKind.Success, "Apri",
                () =>
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return Task.CompletedTask;
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.Error($"Impossibile salvare il file:\n{path}\n\n{ex.Message}", "Esportazione non riuscita");
        }
    }

    // ------------------------------------------------------------------ Strumenti e ?

    [RelayCommand]
    private void OpenDataFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataFolder}\"") { UseShellExecute = true });

    [RelayCommand]
    private void ShowAbout() => _dialogs.ShowAbout();

    /// <summary>Apre la guida; il parametro è (parte del) titolo del capitolo da mostrare.</summary>
    [RelayCommand]
    private void ShowHelp(string? chapter) => _dialogs.ShowHelp(chapter);
}
