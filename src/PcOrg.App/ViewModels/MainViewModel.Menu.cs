using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcOrg.App.Infrastructure;
using PcOrg.Core.Storage;

namespace PcOrg.App.ViewModels;

/// <summary>Comandi e opzioni della barra dei menu.</summary>
public sealed partial class MainViewModel
{
    private AppSettings _settings = new();
    private bool _persistSettings;

    /// <summary>Applica le preferenze salvate (tema, pannelli). Le modifiche successive vengono salvate.</summary>
    public void UseSettings(AppSettings settings)
    {
        _settings = settings;
        _persistSettings = true;
        ShowPreviewPanel = settings.ShowPreviewPanel;
        ShowDetailsPanel = settings.ShowDetailsPanel;
        ApplyTheme(settings.Theme, save: false);
    }

    private void SaveSettings()
    {
        if (_persistSettings)
            _settings.Save();
    }

    // ------------------------------------------------------------------ Visualizza

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPanelVisible))]
    private bool _showPreviewPanel = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsPanelVisible))]
    private bool _showDetailsPanel = true;

    /// <summary>Scheda attiva: 0 = File, 1 = Prodotti e versioni.</summary>
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
            $"pcOrg {DateTime.Now:yyyy-MM-dd}.csv", "File CSV per Excel (*.csv)|*.csv");
        if (path is null)
            return;

        var query = new SearchQuery(SearchText, SelectedCategory?.Category, Limit: 1_000_000)
        {
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
