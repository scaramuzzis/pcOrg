using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using PcOrg.App.Infrastructure;
using PcOrg.App.ViewModels;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Storage;

namespace PcOrg.App.Views;

/// <summary>
/// Finestra principale. La logica è nel <see cref="MainViewModel"/>; qui restano le parti legate
/// ai controlli: selezione nelle griglie, disposizione delle colonne, posizione della finestra,
/// chiusura sicura e apertura delle Impostazioni.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Griglie la cui disposizione delle colonne viene salvata, con il nome usato nelle preferenze.</summary>
    private readonly Dictionary<string, DataGrid> _grids;

    /// <summary>Disposizione originale delle colonne (dal file XAML), per "Ripristina predefinite".</summary>
    private readonly Dictionary<string, List<ColumnSetting>> _defaultColumns = [];

    /// <summary>Salvataggio ritardato: evita di scrivere il file a ogni piccolo spostamento di colonna.</summary>
    private readonly DispatcherTimer _saveLayoutTimer;

    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        _grids = new()
        {
            ["file"] = ResultsGrid,
            ["prodotti"] = ProductsGrid,
            ["fileProdotto"] = ProductFilesGrid,
            ["cloni"] = DuplicateGroupsGrid,
            ["fileCloni"] = DuplicateFilesGrid,
        };
        _saveLayoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveLayoutTimer.Tick += (_, _) =>
        {
            _saveLayoutTimer.Stop();
            SaveLayout();
        };

        CommandBindings.Add(new CommandBinding(FocusCommands.FocusSearch, (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }));
        CommandBindings.Add(new CommandBinding(FocusCommands.OpenSettings, (_, _) => OpenSettings()));

        SourceInitialized += (_, _) => RestorePlacement();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>Nomi delle griglie come appaiono nelle Impostazioni.</summary>
    public static readonly IReadOnlyDictionary<string, string> GridTitles = new Dictionary<string, string>
    {
        ["file"] = "Elenco file",
        ["prodotti"] = "Prodotti",
        ["fileProdotto"] = "File del prodotto selezionato",
        ["cloni"] = "Cloni: gruppi di file identici",
        ["fileCloni"] = "Cloni: copie del file selezionato",
    };

    // ------------------------------------------------------------------ Disposizione salvata

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        foreach (var (name, grid) in _grids)
        {
            _defaultColumns[name] = ColumnLayout.Capture(grid);
            if (ViewModel?.Settings.Columns.TryGetValue(name, out var saved) == true)
                ColumnLayout.Apply(grid, saved);

            // Ogni modifica alle colonne viene salvata (con un piccolo ritardo).
            grid.ColumnReordered += (_, _) => ScheduleSaveLayout();
            grid.Sorting += (_, _) => ScheduleSaveLayout();
            grid.ColumnHeaderStyle = CreateHeaderStyle(name, grid);
        }
        if (ViewModel is { } vm)
        {
            ContentGrid.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(vm.Settings.LeftPanelWidth, 230, 600));
            vm.RequestOpenSettings += tab => OpenSettings(tab);
        }
    }

    private void ScheduleSaveLayout()
    {
        _saveLayoutTimer.Stop();
        _saveLayoutTimer.Start();
    }

    /// <summary>Salva colonne, pannello sinistro e posizione della finestra nelle preferenze.</summary>
    public void SaveLayout()
    {
        if (ViewModel is not { } vm)
            return;
        foreach (var (name, grid) in _grids)
            vm.Settings.Columns[name] = ColumnLayout.Capture(grid);
        vm.Settings.LeftPanelWidth = ContentGrid.ColumnDefinitions[0].ActualWidth;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        vm.Settings.Window = new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
        vm.SaveSettings();
    }

    /// <summary>Riapre la finestra dove l'utente l'aveva lasciata, se quella posizione è ancora sullo schermo.</summary>
    private void RestorePlacement()
    {
        if (ViewModel?.Settings.Window is not { Width: > 300, Height: > 200 } p)
            return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var title = new Rect(p.Left, p.Top, p.Width, 40);
        if (!screen.IntersectsWith(title))
            return; // es. il secondo monitor non è più collegato
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = p.Left;
        Top = p.Top;
        Width = p.Width;
        Height = p.Height;
        if (p.Maximized)
            WindowState = WindowState.Maximized;
    }

    // ------------------------------------------------------------------ Colonne: menu sulle intestazioni

    /// <summary>
    /// Stile delle intestazioni con un menu (tasto destro) per mostrare o nascondere le colonne,
    /// ripristinare quelle predefinite o aprire la configurazione completa.
    /// </summary>
    private Style CreateHeaderStyle(string name, DataGrid grid)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            foreach (var column in grid.Columns.OrderBy(c => c.DisplayIndex))
            {
                var item = new MenuItem
                {
                    Header = ColumnLayout.KeyOf(column),
                    IsCheckable = true,
                    IsChecked = column.Visibility == Visibility.Visible,
                    IsEnabled = ColumnLayout.CanHide(grid, column),
                };
                item.Click += (_, _) =>
                {
                    column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                    ScheduleSaveLayout();
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var reset = new MenuItem { Header = "Ripristina le colonne predefinite" };
            reset.Click += (_, _) => ResetColumns(name);
            menu.Items.Add(reset);
            var configure = new MenuItem { Header = "Configura le colonne…" };
            configure.Click += (_, _) => OpenSettings(SettingsWindow.ColumnsTab, name);
            menu.Items.Add(configure);
        };

        var style = new Style(typeof(DataGridColumnHeader),
            TryFindResource(typeof(DataGridColumnHeader)) as Style);
        style.Setters.Add(new Setter(ContextMenuProperty, menu));
        style.Setters.Add(new Setter(ToolTipProperty, "Trascina per spostare la colonna · Tasto destro per scegliere le colonne"));
        return style;
    }

    /// <summary>Riporta le colonne di una griglia alla disposizione originale.</summary>
    public void ResetColumns(string name)
    {
        if (!_grids.TryGetValue(name, out var grid) || !_defaultColumns.TryGetValue(name, out var defaults))
            return;
        foreach (var column in grid.Columns)
            column.SortDirection = null;
        grid.Items.SortDescriptions.Clear();
        ColumnLayout.Apply(grid, defaults);
        ScheduleSaveLayout();
    }

    // ------------------------------------------------------------------ Impostazioni

    private void ColumnsSettings_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsWindow.ColumnsTab);

    /// <summary>Apre la finestra Impostazioni, eventualmente su una scheda e una griglia precise.</summary>
    public void OpenSettings(int tab = 0, string? grid = null)
    {
        if (ViewModel is not { } vm)
            return;
        var window = new SettingsWindow(vm, this, _grids, tab, grid) { Owner = this };
        window.ShowDialog();
    }

    /// <summary>Disposizione originale di una griglia (per le Impostazioni).</summary>
    public IReadOnlyList<ColumnSetting>? DefaultColumns(string name) =>
        _defaultColumns.TryGetValue(name, out var list) ? list : null;

    // ------------------------------------------------------------------ Chiusura

    /// <summary>
    /// Chiusura sicura: se è in corso una scansione chiede conferma, la interrompe e attende che
    /// l'ultimo blocco di file venga salvato prima di uscire. Salva sempre la disposizione.
    /// </summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveLayout();
        if (_closingConfirmed || ViewModel is not { IsBusy: true } vm)
        {
            ViewModel?.Shutdown();
            return;
        }

        e.Cancel = true;
        var stop = ModernDialog.Show(this, "Uscire durante l'aggiornamento?",
            "pcOrg sta ancora aggiornando l'indice. I file trovati finora sono già salvati: se esci ora, " +
            "il prossimo aggiornamento riprenderà da lì.", DialogKind.Question, "Interrompi ed esci", "Continua");
        if (!stop)
            return;

        Log.Info("Chiusura richiesta durante l'aggiornamento: interruzione in corso.");
        vm.CancelScanCommand.Execute(null);
        await vm.WaitUntilIdleAsync(TimeSpan.FromSeconds(10));
        vm.Shutdown();
        _closingConfirmed = true;
        Close();
    }

    // ------------------------------------------------------------------ Selezione e azioni sulle griglie

    private void FileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.SelectedFiles = ResultsGrid.SelectedItems.OfType<FileRecord>().ToList();
    }

    private void ProductFilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.SelectedFiles = ProductFilesGrid.SelectedItems.OfType<ProductFileItem>().Select(i => i.File).ToList();
    }

    private void DuplicateFilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.SelectedFiles = DuplicateFilesGrid.SelectedItems.OfType<DuplicateFileItem>().Select(i => i.File).ToList();
    }

    private void FileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Il doppio clic su un'intestazione di colonna non deve aprire il file selezionato.
        if (e.OriginalSource is DependencyObject source && FindParent<DataGridColumnHeader>(source) is not null)
            return;
        var file = (sender as DataGrid)?.SelectedItem switch
        {
            FileRecord f => f,
            ProductFileItem item => item.File,
            DuplicateFileItem copy => copy.File,
            _ => null,
        };
        if (file is not null)
            ViewModel?.OpenFileCommand.Execute(file);
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var current = child; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is T found)
                return found;
        return null;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var grid = ViewModel?.SelectedTabIndex switch { 1 => ProductFilesGrid, 2 => DuplicateFilesGrid, _ => ResultsGrid };
        grid.SelectAll();
        grid.Focus();
    }

    /// <summary>Invio apre il file selezionato (invece di passare alla riga successiva).</summary>
    private void FileGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ResultsGrid.SelectedItem is FileRecord file)
        {
            ViewModel?.OpenFileCommand.Execute(file);
            e.Handled = true;
        }
    }

    /// <summary>Seleziona tutte le versioni precedenti all'ultima del prodotto scelto.</summary>
    private void SelectOldVersions_Click(object sender, RoutedEventArgs e)
    {
        ProductFilesGrid.SelectedItems.Clear();
        foreach (var item in ProductFilesGrid.Items.OfType<ProductFileItem>().Where(i => i.IsOld))
            ProductFilesGrid.SelectedItems.Add(item);
        ProductFilesGrid.Focus();
    }
}
