using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using PcOrg.App.Infrastructure;
using PcOrg.App.ViewModels;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Classification;
using PcOrg.Core.Rules;
using Microsoft.Win32;

namespace PcOrg.App.Views;

/// <summary>
/// Finestra Impostazioni: opzioni generali, colonne visibili e loro ordine per ogni elenco,
/// informazioni e manutenzione dell'indice. Le modifiche si applicano con "Salva".
/// </summary>
public partial class SettingsWindow : Window
{
    public const int GeneralTab = 0;
    public const int ColumnsTab = 1;
    public const int RulesTab = MainViewModel.RulesTabIndex;
    public const int DataTab = 3;
    public const int CategoriesTab = 4;

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

    /// <summary>Copia di lavoro delle regole: diventa effettiva solo con "Salva".</summary>
    private readonly ObservableCollection<ArchiveRule> _rules;

    /// <summary>Copia di lavoro delle colonne di ogni elenco: diventa effettiva solo con "Salva".</summary>
    private readonly Dictionary<string, ObservableCollection<ColumnRow>> _columns = [];

    /// <summary>Categorie con la spunta "mostra nel pannello": diventa effettiva solo con "Salva".</summary>
    private readonly List<(FileCategory Category, ColumnRow Row)> _categories = [];

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
        WatchFolders.IsChecked = settings.WatchFolders;
        LogEnabled.IsChecked = settings.LogEnabled;
        LogLevelChooser.SelectedItem = LogLevelChooser.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == settings.LogLevel.ToString()) ?? LogLevelChooser.Items[2];
        VerifySignatures.IsChecked = settings.VerifySignatures;
        IndexContent.IsChecked = settings.IndexContent;
        CheckUpdates.IsChecked = settings.CheckUpdatesOnStartup;
        ShowPreview.IsChecked = vm.ShowPreviewPanel;
        ShowDetails.IsChecked = vm.ShowDetailsPanel;

        // Categorie: una riga per categoria, in ordine alfabetico come nel pannello
        foreach (var category in Enum.GetValues<FileCategory>().OrderBy(c => c.DisplayName()))
            _categories.Add((category, new ColumnRow { Key = category.DisplayName(), CanHide = true, Visible = !vm.IsCategoryHidden(category) }));
        CategoryList.ItemsSource = _categories.Select(c => c.Row).ToList();

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

        // Regole: copie indipendenti, così "Annulla" non modifica quelle in uso
        _rules = new ObservableCollection<ArchiveRule>(vm.Rules.Select(Clone));
        RuleList.ItemsSource = _rules;

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

    // ------------------------------------------------------------------ Regole

    private static ArchiveRule Clone(ArchiveRule r) => new()
    {
        Name = r.Name, Enabled = r.Enabled, Category = r.Category, CompanyContains = r.CompanyContains,
        NameContains = r.NameContains, Destination = r.Destination,
    };

    private void NewRule_Click(object sender, RoutedEventArgs e)
    {
        var rule = new ArchiveRule { Name = $"Regola {_rules.Count + 1}" };
        if (new RuleEditorWindow(rule) { Owner = this }.ShowDialog() == true)
        {
            _rules.Add(rule);
            RuleList.SelectedItem = rule;
        }
    }

    private void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not ArchiveRule rule)
            return;
        var copy = Clone(rule);
        if (new RuleEditorWindow(copy) { Owner = this }.ShowDialog() == true)
        {
            var index = _rules.IndexOf(rule);
            _rules[index] = copy;
            RuleList.SelectedIndex = index;
        }
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is ArchiveRule rule)
            _rules.Remove(rule);
    }

    private void RuleUp_Click(object sender, RoutedEventArgs e) => MoveRule(-1);
    private void RuleDown_Click(object sender, RoutedEventArgs e) => MoveRule(+1);

    private void MoveRule(int delta)
    {
        var index = RuleList.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _rules.Count)
            return;
        _rules.Move(index, target);
        RuleList.SelectedIndex = target;
    }

    /// <summary>Crea regole pronte all'uso dentro una cartella di archivio scelta dall'utente.</summary>
    private void SampleRules_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Scegli la cartella di archivio (es. D:\\Archivio)" };
        if (dialog.ShowDialog(this) != true)
            return;
        var root = dialog.FolderName;
        foreach (var rule in SampleRules(root))
            _rules.Add(rule);
    }

    /// <summary>Insieme di regole di esempio con una struttura di archivio ordinata.</summary>
    public static IEnumerable<ArchiveRule> SampleRules(string root) =>
    [
        new() { Name = "Driver per produttore", Category = FileCategory.Driver, Destination = Path.Combine(root, "Driver", "{Produttore}") },
        new() { Name = "Installer per produttore e prodotto", Category = FileCategory.Installer, Destination = Path.Combine(root, "Installer", "{Produttore}", "{Prodotto}") },
        new() { Name = "Immagini disco per tipo", Category = FileCategory.ImmagineDisco, Destination = Path.Combine(root, "Immagini disco", "{Estensione}") },
        new() { Name = "Connessioni remote", Category = FileCategory.AccessoRemoto, Destination = Path.Combine(root, "Connessioni remote") },
        new() { Name = "PDF per anno", Category = FileCategory.Pdf, Destination = Path.Combine(root, "Documenti", "PDF", "{Anno}") },
        new() { Name = "Documenti Word per anno", Category = FileCategory.Word, Destination = Path.Combine(root, "Documenti", "Word", "{Anno}") },
    ];

    // ------------------------------------------------------------------ Dati e registro

    private void RefreshDataInfo()
    {
        var stats = _vm.GetIndexStats();
        DatabaseText.Text = stats.DatabasePath;
        DatabaseText.ToolTip = stats.DatabasePath;
        ContentText.Text = $"{stats.Files:N0} file in {stats.Roots:N0} cartelle · {stats.MoveBatches:N0} spostamenti in cronologia · {FileSizeConverter.Format(stats.DatabaseBytes)}";
        SettingsText.Text = _vm.Settings.FilePath ?? "(non salvate)";
        LogText.Text = Log.FilePath is null ? "(non attivo)"
            : Log.Enabled ? Log.FilePath : $"{Log.FilePath} (disattivato)";
        var busy = _vm.IsBusy;
        CompactButton.IsEnabled = !busy;
        RebuildButton.IsEnabled = !busy;
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => _vm.OpenDataFolderCommand.Execute(null);
    private void OpenLog_Click(object sender, RoutedEventArgs e) => _vm.OpenLogCommand.Execute(null);

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        Log.Clear();
        Log.Info("Registro svuotato dall'utente.");
        RefreshDataInfo();
    }

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

    // ------------------------------------------------------------------ Categorie

    private void AllCategories_Click(object sender, RoutedEventArgs e) => _categories.ForEach(c => c.Row.Visible = true);

    private void NoCategories_Click(object sender, RoutedEventArgs e) => _categories.ForEach(c => c.Row.Visible = false);

    // ------------------------------------------------------------------ Salva

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var theme = ThemeDark.IsChecked == true ? "Scuro" : ThemeLight.IsChecked == true ? "Chiaro" : "Sistema";
        ApplyColumns();
        _vm.SetRules(_rules);
        _vm.SetHiddenCategories(_categories.Where(c => !c.Row.Visible).Select(c => c.Category));
        var level = Enum.TryParse<LogLevel>((LogLevelChooser.SelectedItem as ComboBoxItem)?.Tag as string, out var l) ? l : LogLevel.Info;
        _vm.ApplyOptions(new MainViewModel.Options(theme,
            ScanOnStartup: ScanOnStartup.IsChecked == true,
            WatchFolders: WatchFolders.IsChecked == true,
            VerifySignatures: VerifySignatures.IsChecked == true,
            ShowPreview: ShowPreview.IsChecked == true,
            ShowDetails: ShowDetails.IsChecked == true,
            LogEnabled: LogEnabled.IsChecked == true,
            LogLevel: level,
            IndexContent: IndexContent.IsChecked == true,
            CheckUpdatesOnStartup: CheckUpdates.IsChecked == true));
        _main.SaveLayout();
        DialogResult = true;
    }
}
