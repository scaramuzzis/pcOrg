using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcOrg.App.Infrastructure;
using PcOrg.Core;
using PcOrg.Core.Classification;
using PcOrg.Core.Indexing;
using PcOrg.Core.Metadata;
using PcOrg.Core.Moving;
using PcOrg.Core.Storage;

namespace PcOrg.App.ViewModels;

/// <summary>
/// Voce del pannello categorie ("Tutti" ha Category = null). Le sottocategorie (es. Ghost sotto
/// Immagini disco) hanno l'elenco delle estensioni e vengono mostrate rientrate.
/// </summary>
public sealed record CategoryItem(FileCategory? Category, string Name, long Count, long TotalSize,
    IReadOnlyList<string>? Extensions = null)
{
    public bool IsSubcategory => Extensions is not null;
}

/// <summary>Riga dell'elenco dei file di un prodotto.</summary>
public sealed record ProductFileItem(FileRecord File, string State, bool IsOld);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly FileIndex _index;
    private readonly IDialogService _dialogs;
    private readonly MoveService _moves;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _scanCts;

    public MainViewModel(FileIndex index, IDialogService dialogs)
    {
        _index = index;
        _dialogs = dialogs;
        _moves = new MoveService(index);
        ProductsView = CollectionViewSource.GetDefaultView(Products);
        ProductsView.Filter = FilterProduct;
        ReloadRoots();
        _ = StartupAsync();
    }

    private async Task StartupAsync()
    {
        await RefreshAsync();
        // Es. indice creato con la versione 0.1: legge subito versioni e firme mancanti.
        if (await Task.Run(_index.CountFilesNeedingMetadata) > 0)
            await UpdateMetadataAsync();
    }

    public ObservableCollection<RootFolder> Roots { get; } = [];
    public ObservableCollection<CategoryItem> Categories { get; } = [];
    public ObservableCollection<FileRecord> Results { get; } = [];

    public ObservableCollection<ProductGroup> Products { get; } = [];
    public ICollectionView ProductsView { get; }
    public ObservableCollection<ProductFileItem> ProductFiles { get; } = [];

    /// <summary>Righe selezionate nell'ultima griglia usata (aggiornate dalla vista).</summary>
    public IReadOnlyList<FileRecord> SelectedFiles
    {
        get => _selectedFiles;
        set
        {
            _selectedFiles = value;
            SelectedFile = value.FirstOrDefault();
        }
    }
    private IReadOnlyList<FileRecord> _selectedFiles = [];

    /// <summary>File mostrato nel pannello dei dettagli.</summary>
    [ObservableProperty]
    private FileRecord? _selectedFile;

    [ObservableProperty]
    private ProductGroup? _selectedProduct;

    [ObservableProperty]
    private bool _onlyMultipleVersions = true;

    [ObservableProperty]
    private string _productsSummary = "";

    partial void OnSelectedProductChanged(ProductGroup? value)
    {
        ProductFiles.Clear();
        if (value is null)
            return;
        foreach (var file in value.Files)
        {
            var old = value.IsOld(file);
            var state = value.IsLatest(file) ? "Più recente" : old ? "Versione vecchia" : "";
            ProductFiles.Add(new ProductFileItem(file, state, old));
        }
    }

    partial void OnOnlyMultipleVersionsChanged(bool value) => ProductsView.Refresh();

    private bool FilterProduct(object item)
    {
        if (item is not ProductGroup group)
            return false;
        if (OnlyMultipleVersions && group.Files.Count < 2)
            return false;
        var text = SearchText.Trim();
        return text.Length == 0
            || group.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
            || (group.Company?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveRootCommand))]
    private RootFolder? _selectedRoot;

    [ObservableProperty]
    private CategoryItem? _selectedCategory;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(AddRootCommand), nameof(RemoveRootCommand),
        nameof(MoveSelectedCommand), nameof(CancelScanCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Pronto";

    partial void OnSearchTextChanged(string value)
    {
        _ = RefreshResultsAsync(debounce: true);
        ProductsView.Refresh();
    }

    partial void OnSelectedCategoryChanged(CategoryItem? value) => _ = RefreshResultsAsync(debounce: false);

    // ------------------------------------------------------------------ Cartelle monitorate

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task AddRootAsync()
    {
        var folder = _dialogs.PickFolder("Scegli una cartella da organizzare");
        if (folder is null)
            return;
        try
        {
            var root = _index.AddRoot(folder);
            ReloadRoots();
            await ScanRootsAsync([root]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _dialogs.Error(ex.Message, "Cartella non aggiunta");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveRoot))]
    private async Task RemoveRootAsync()
    {
        if (SelectedRoot is not { } root)
            return;
        if (!_dialogs.Confirm(
                $"Smettere di monitorare \"{root.Path}\"?\n\nI file restano dove sono: vengono solo tolti dall'elenco di pcOrg.",
                "Rimuovi cartella"))
            return;
        _index.RemoveRoot(root.Id);
        ReloadRoots();
        await RefreshAsync();
    }

    private bool CanModify() => !IsBusy;
    private bool CanRemoveRoot() => !IsBusy && SelectedRoot is not null;

    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task ScanAsync() => ScanRootsAsync(Roots.ToList());

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void CancelScan() => _scanCts?.Cancel();

    private async Task ScanRootsAsync(IReadOnlyList<RootFolder> roots)
    {
        if (roots.Count == 0)
        {
            Status = "Aggiungi una cartella per iniziare.";
            return;
        }

        IsBusy = true;
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;
        var total = 0;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            foreach (var root in roots)
            {
                if (!Directory.Exists(root.Path))
                {
                    Status = $"Cartella non raggiungibile, saltata: {root.Path}";
                    continue;
                }
                var progress = new Progress<int>(n => Status = $"Scansione di {root.Path}: {n:N0} file…");
                total += await Task.Run(
                    () => _index.SyncRoot(root.Id, FolderScanner.Enumerate(root.Path, token), progress, token), token);
            }
            Status = $"Scansione completata: {total:N0} file in {stopwatch.Elapsed.TotalSeconds:N1} s";
        }
        catch (OperationCanceledException)
        {
            Status = "Scansione interrotta.";
            return;
        }
        finally
        {
            IsBusy = false;
            _scanCts.Dispose();
            _scanCts = null;
            ReloadRoots();
            await RefreshAsync();
        }
        await UpdateMetadataAsync();
    }

    /// <summary>Legge versione, produttore, architettura e firma dei file nuovi o modificati.</summary>
    private async Task UpdateMetadataAsync()
    {
        IsBusy = true;
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;
        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
                Status = $"Lettura di versioni e firme: {p.Done:N0} di {p.Total:N0} file…");
            var count = await Task.Run(() => MetadataUpdater.Run(_index, progress, token), token);
            Status = count == 0 ? "Indice aggiornato." : $"Lette versioni e firme di {count:N0} file.";
        }
        catch (OperationCanceledException)
        {
            Status = "Lettura di versioni e firme interrotta: riprenderà al prossimo aggiornamento.";
        }
        finally
        {
            IsBusy = false;
            _scanCts.Dispose();
            _scanCts = null;
        }
        await RefreshAsync();
    }

    private void ReloadRoots()
    {
        var selected = SelectedRoot?.Id;
        Roots.Clear();
        foreach (var root in _index.GetRoots())
            Roots.Add(root);
        SelectedRoot = Roots.FirstOrDefault(r => r.Id == selected);
    }

    // ------------------------------------------------------------------ Categorie e ricerca

    private async Task RefreshAsync()
    {
        var stats = await Task.Run(_index.GetCategoryStats);
        var selected = SelectedCategory;
        await RefreshProductsAsync();

        Categories.Clear();
        Categories.Add(new CategoryItem(null, "Tutti", stats.Sum(s => s.Count), stats.Sum(s => s.TotalSize)));
        foreach (var category in Enum.GetValues<FileCategory>().OrderBy(c => c.DisplayName()))
        {
            var stat = stats.FirstOrDefault(s => s.Category == category);
            if (stat is null)
                continue;
            Categories.Add(new CategoryItem(category, category.DisplayName(), stat.Count, stat.TotalSize));

            var subcategories = Subcategories.For(category).ToList();
            if (subcategories.Count == 0)
                continue;
            var byExtension = await Task.Run(() => _index.GetExtensionStats(category));
            foreach (var sub in subcategories)
            {
                var matching = byExtension.Where(e => sub.Extensions.Contains(e.Extension, StringComparer.OrdinalIgnoreCase)).ToList();
                if (matching.Count > 0)
                    Categories.Add(new CategoryItem(category, sub.Name, matching.Sum(e => e.Count), matching.Sum(e => e.TotalSize), sub.Extensions));
            }
        }
        // Reimpostare la selezione rilancia la ricerca (anche se la voce selezionata è identica).
#pragma warning disable MVVMTK0034
        _selectedCategory = null;
#pragma warning restore MVVMTK0034
        SelectedCategory = Categories.FirstOrDefault(c => c.Category == selected?.Category && c.Name == selected?.Name)
            ?? Categories.FirstOrDefault(c => c.Category == selected?.Category && !c.IsSubcategory)
            ?? Categories[0];
    }

    private async Task RefreshProductsAsync()
    {
        var selectedKey = SelectedProduct?.Key;
        var groups = await Task.Run(() => ProductCatalog.Build(_index.GetProductFiles()));
        Products.Clear();
        foreach (var group in groups)
            Products.Add(group);
        SelectedProduct = Products.FirstOrDefault(p => p.Key == selectedKey);

        var multi = groups.Where(g => g.Files.Count > 1).ToList();
        var reclaimable = multi.Sum(g => g.OldVersionsSize);
        ProductsSummary = $"{groups.Count:N0} prodotti, {multi.Count:N0} con più file. "
            + $"Le versioni vecchie occupano {FileSizeConverter.Format(reclaimable)}.";
    }

    private async Task RefreshResultsAsync(bool debounce)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            if (debounce)
                await Task.Delay(150, cts.Token);
            var query = new SearchQuery(SearchText, SelectedCategory?.Category) { Extensions = SelectedCategory?.Extensions };
            var stopwatch = Stopwatch.StartNew();
            var results = await Task.Run(() => _index.Search(query), cts.Token);
            if (cts.IsCancellationRequested)
                return;

            Results.Clear();
            foreach (var file in results)
                Results.Add(file);
            if (!IsBusy)
                Status = results.Count >= query.Limit
                    ? $"Mostrati i primi {results.Count:N0} risultati ({stopwatch.ElapsedMilliseconds} ms): affina la ricerca"
                    : $"{results.Count:N0} file ({stopwatch.ElapsedMilliseconds} ms)";
        }
        catch (OperationCanceledException)
        {
            // superata da una ricerca più recente
        }
    }

    // ------------------------------------------------------------------ Azioni sui file

    [RelayCommand]
    private void OpenFile(FileRecord? file)
    {
        file ??= SelectedFiles.FirstOrDefault();
        if (file is null)
            return;
        if (!File.Exists(file.Path))
        {
            _dialogs.Error($"Il file non esiste più:\n{file.Path}\n\nAggiorna l'indice.", "File non trovato");
            return;
        }
        var runsCode = file.Category is FileCategory.Installer or FileCategory.Programma
            or FileCategory.Driver or FileCategory.Script;
        if (runsCode && !_dialogs.Confirm($"Eseguire \"{file.Name}\"?", "Esegui file"))
            return;
        Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (SelectedFiles.FirstOrDefault() is not { } file)
            return;
        // Apre Esplora risorse con il file già selezionato.
        Process.Start("explorer.exe", $"/select,\"{file.Path}\"");
    }

    [RelayCommand]
    private void CopyPath()
    {
        if (SelectedFiles.Count > 0)
            Clipboard.SetText(string.Join(Environment.NewLine, SelectedFiles.Select(f => f.Path)));
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task MoveSelectedAsync()
    {
        var files = SelectedFiles.ToList();
        if (files.Count == 0)
        {
            _dialogs.Info("Seleziona uno o più file da spostare.", "Sposta");
            return;
        }

        var destination = _dialogs.PickFolder($"Dove vuoi spostare {files.Count} file?");
        if (destination is null)
            return;

        var plan = MovePlanner.Plan(files.Select(f => f.Path), destination);
        if (!plan.Any(p => p.WillMove))
        {
            _dialogs.Info("Nessun file da spostare: sono già nella cartella scelta o non esistono più.", "Sposta");
            return;
        }

        var monitored = _index.FindRootFor(destination) is not null;
        if (_dialogs.PreviewMove(plan, destination, monitored) is not { } choice)
            return;

        IsBusy = true;
        try
        {
            if (!monitored && choice.AddDestinationToMonitored)
            {
                try
                {
                    _index.AddRoot(destination);
                    ReloadRoots();
                }
                catch (InvalidOperationException ex)
                {
                    _dialogs.Error(ex.Message, "Cartella non aggiunta");
                }
            }

            var count = plan.Count(p => p.WillMove);
            var progress = new Progress<int>(n => Status = $"Spostamento {n:N0} di {count:N0}…");
            var description = $"{count} file in {destination}";
            var result = await Task.Run(() => _moves.Execute(plan, description, progress));

            Status = $"Spostati {result.Moved:N0} file in {destination}";
            if (result.Errors.Count > 0)
                _dialogs.Error(
                    $"{result.Errors.Count} file non sono stati spostati:\n\n" + string.Join("\n", result.Errors.Take(15)),
                    "Spostamento incompleto");
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private void ShowHistory() => _dialogs.ShowMoveHistory(_index, () => _ = RefreshAsync());
}
