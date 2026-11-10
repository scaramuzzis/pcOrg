using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using PcOrg.App.Infrastructure;
using PcOrg.App.ViewModels;
using PcOrg.Core.Diagnostics;

namespace PcOrg.App.Views;

/// <summary>
/// Finestra Impostazioni: opzioni generali, colonne visibili e loro ordine per ogni elenco,
/// informazioni e manutenzione dell'indice. Le modifiche si applicano con "Salva".
/// </summary>
public partial class SettingsWindow : Window
{
    public const int GeneralTab = 0;
    public const int ColumnsTab = 1;
    public const int DataTab = 2;

    /// <summary>Una riga dell'elenco colonne: nome, visibilità e se può essere nascosta.</summary>
    public sealed partial class ColumnRow : ObservableObject
    {
        public required string Key { get; init; }
        public required bool CanHide { get; init; }

        [ObservableProperty]
        private bool _visible;
    }

    private readonly MainViewModel _vm;
    private readonly MainWindow _main;
    private readonly IReadOnlyDictionary<string, DataGrid> _grids;

    /// <summary>Copia di lavoro delle colonne di ogni elenco: diventa effettiva solo con "Salva".</summary>
    private readonly Dictionary<string, ObservableCollection<ColumnRow>> _columns = [];

    public SettingsWindow(MainViewModel vm, MainWindow main, IReadOnlyDictionary<string, DataGrid> grids, int tab = GeneralTab, string? grid = null)
    {
        InitializeComponent();
        _vm = vm;
        _main = main;
        _grids = grids;

        // Generale
        var settings = vm.Settings;
        ThemeSystem.IsChecked = vm.IsThemeSystem;
        ThemeLight.IsChecked = vm.IsThemeLight;
        ThemeDark.IsChecked = vm.IsThemeDark;
        ScanOnStartup.IsChecked = settings.ScanOnStartup;
        VerifySignatures.IsChecked = settings.VerifySignatures;
        ShowPreview.IsChecked = vm.ShowPreviewPanel;
        ShowDetails.IsChecked = vm.ShowDetailsPanel;

        // Colonne: si parte dallo stato attuale delle griglie, nell'ordine in cui sono mostrate
        foreach (var (name, dataGrid) in grids)
        {
            _columns[name] = new ObservableCollection<ColumnRow>(
                dataGrid.Columns.OrderBy(c => c.DisplayIndex).Select(c => new ColumnRow
                {
                    Key = ColumnLayout.KeyOf(c),
                    CanHide = ColumnLayout.CanHide(dataGrid, c),
                    Visible = c.Visibility == Visibility.Visible,
                }));
            GridChooser.Items.Add(new ComboBoxItem { Content = MainWindow.GridTitles[name], Tag = name });
        }
        GridChooser.SelectedIndex = Math.Max(0, grids.Keys.ToList().IndexOf(grid ?? "file"));

        RefreshDataInfo();
        Tabs.SelectedIndex = tab;
    }

    private string CurrentGrid => (GridChooser.SelectedItem as ComboBoxItem)?.Tag as string ?? "file";

    // ------------------------------------------------------------------ Colonne

    private void GridChooser_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ColumnList.ItemsSource = _columns[CurrentGrid];

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        var list = _columns[CurrentGrid];
        var index = ColumnList.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count)
            return;
        list.Move(index, target);
        ColumnList.SelectedIndex = target;
        ColumnList.ScrollIntoView(ColumnList.SelectedItem);
    }

    private void ResetColumns_Click(object sender, RoutedEventArgs e)
    {
        if (_main.DefaultColumns(CurrentGrid) is not { } defaults)
            return;
        var grid = _grids[CurrentGrid];
        _columns[CurrentGrid] = new ObservableCollection<ColumnRow>(defaults.OrderBy(d => d.DisplayIndex).Select(d => new ColumnRow
        {
            Key = d.Key,
            Visible = d.Visible,
            CanHide = grid.Columns.FirstOrDefault(c => ColumnLayout.KeyOf(c) == d.Key) is { } column && ColumnLayout.CanHide(grid, column),
        }));
        ColumnList.ItemsSource = _columns[CurrentGrid];
    }

    /// <summary>Applica alle griglie visibilità e ordine scelti, mantenendo larghezze e ordinamento attuali.</summary>
    private void ApplyColumns()
    {
        foreach (var (name, rows) in _columns)
        {
            var grid = _grids[name];
            var current = ColumnLayout.Capture(grid).ToDictionary(c => c.Key);
            var updated = rows.Select((row, index) =>
            {
                var setting = current.TryGetValue(row.Key, out var c) ? c : new ColumnSetting { Key = row.Key };
                setting.Visible = row.Visible;
                setting.DisplayIndex = index;
                return setting;
            }).ToList();
            ColumnLayout.Apply(grid, updated);
        }
    }

    // ------------------------------------------------------------------ Dati e registro

    private void RefreshDataInfo()
    {
        var stats = _vm.GetIndexStats();
        DatabaseText.Text = stats.DatabasePath;
        DatabaseText.ToolTip = stats.DatabasePath;
        ContentText.Text = $"{stats.Files:N0} file in {stats.Roots:N0} cartelle · {stats.MoveBatches:N0} spostamenti in cronologia · {FileSizeConverter.Format(stats.DatabaseBytes)}";
        SettingsText.Text = _vm.Settings.FilePath ?? "(non salvate)";
        LogText.Text = Log.FilePath ?? "(non attivo)";
        var busy = _vm.IsBusy;
        CompactButton.IsEnabled = !busy;
        RebuildButton.IsEnabled = !busy;
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => _vm.OpenDataFolderCommand.Execute(null);
    private void OpenLog_Click(object sender, RoutedEventArgs e) => _vm.OpenLogCommand.Execute(null);

    private async void Compact_Click(object sender, RoutedEventArgs e)
    {
        await _vm.CompactIndexAsync();
        RefreshDataInfo();
    }

    private void Rebuild_Click(object sender, RoutedEventArgs e)
    {
        if (!ModernDialog.Show(this, "Ricostruire l'indice?",
                "L'elenco dei file verrà svuotato e tutte le cartelle monitorate verranno analizzate di nuovo. " +
                "Cartelle, cronologia degli spostamenti e preferenze restano; i file su disco non vengono toccati.",
                DialogKind.Question, "Ricostruisci", "Annulla"))
            return;
        Save_Click(sender, e);
        _ = _vm.RebuildIndexAsync();
    }

    // ------------------------------------------------------------------ Salva

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var theme = ThemeDark.IsChecked == true ? "Scuro" : ThemeLight.IsChecked == true ? "Chiaro" : "Sistema";
        ApplyColumns();
        _vm.ApplyOptions(theme, ScanOnStartup.IsChecked == true, VerifySignatures.IsChecked == true,
            ShowPreview.IsChecked == true, ShowDetails.IsChecked == true);
        _main.SaveLayout();
        DialogResult = true;
    }
}
