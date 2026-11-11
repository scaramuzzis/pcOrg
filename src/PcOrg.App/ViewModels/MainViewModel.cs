using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcOrg.App.Infrastructure;
using PcOrg.Core;
using PcOrg.Core.Classification;
using PcOrg.Core.Diagnostics;
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
    public string Glyph => Glyphs.ForCategory(Category);
    public Brush Accent => CategoryColors.For(Category);
}

/// <summary>Tipo di notifica a comparsa: determina icona e colore.</summary>
public enum ToastKind { Success, Info, Warning }

/// <summary>Riga dell'elenco dei file di un prodotto.</summary>
public sealed record ProductFileItem(FileRecord File, string State, bool IsOld);

/// <summary>
/// ViewModel della finestra principale (schema MVVM con CommunityToolkit.Mvvm): cartelle monitorate,
/// categorie, ricerca, prodotti, anteprima, spostamenti e notifiche. Tutto il lavoro pesante
/// (scansioni, query, letture dei file) gira in background con Task.Run; l'interfaccia resta reattiva.
/// I comandi della barra dei menu e le impostazioni sono in MainViewModel.Menu.cs.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly FileIndex _index;
    private readonly IDialogService _dialogs;
    private readonly MoveService _moves;
    /// <summary>Thread dell'interfaccia: gli eventi dell'osservatore arrivano da altri thread.</summary>
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _scanCts;

    public MainViewModel(FileIndex index, IDialogService dialogs)
    {
        _index = index;
        _dialogs = dialogs;
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _moves = new MoveService(index);
        ProductsView = CollectionViewSource.GetDefaultView(Products);
        ProductsView.Filter = FilterProduct;
        SelectedSizeOption = SizeOptions[1];
        SelectedKeepRule = KeepRules[0];
        ReloadRoots();
        Fire(StartupAsync(), "avvio");
    }

    /// <summary>
    /// Avvio: mostra subito l'elenco salvato nell'indice, poi (se abilitato nelle impostazioni)
    /// aggiorna le cartelle in background, altrimenti legge solo i metadati mancanti.
    /// </summary>
    private async Task StartupAsync()
    {
        await RefreshAsync();
        var stats = await Task.Run(_index.GetStats);
        Status = stats.Roots == 0
            ? "Aggiungi una cartella per iniziare."
            : $"Indice caricato: {stats.Files:N0} file in {stats.Roots:N0} cartelle";
        Log.Info($"Indice caricato da {stats.DatabasePath}: {stats.Files:N0} file, {stats.Roots:N0} cartelle, " +
                 $"{FileSizeConverter.Format(stats.DatabaseBytes)}.");

        // Si aspetta il primo disegno della finestra, così l'utente vede subito i dati.
        await Task.Delay(800);
        await RefreshInstalledAsync();
        if (_scanOnStartup && Roots.Count > 0)
            await ScanRootsAsync(Roots.Select(r => r.Root).ToList());
        else if (await Task.Run(_index.CountFilesNeedingMetadata) > 0)
            await UpdateMetadataAsync(); // es. indice creato con una versione precedente
    }

    /// <summary>
    /// Avvia un'operazione asincrona senza attenderla, registrando gli eventuali errori
    /// (altrimenti un'eccezione in un task non atteso andrebbe persa in silenzio).
    /// </summary>
    private void Fire(Task task, string what)
    {
        task.ContinueWith(t =>
        {
            var error = t.Exception?.GetBaseException();
            Log.Error($"Errore durante: {what}", error);
            ShowToast($"Si è verificato un problema ({what}). Dettagli nel registro attività.", ToastKind.Warning);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
           SynchronizationContext.Current is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Attende che scansioni e spostamenti in corso finiscano (usato alla chiusura).</summary>
    public async Task<bool> WaitUntilIdleAsync(TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (IsBusy && stopwatch.Elapsed < timeout)
            await Task.Delay(100);
        return !IsBusy;
    }

    public ObservableCollection<RootItem> Roots { get; } = [];

    /// <summary>Falso finché non c'è nessuna cartella: mostra la schermata di benvenuto.</summary>
    public bool HasRoots => Roots.Count > 0;

    // ------------------------------------------------------------------ Notifiche a comparsa

    [ObservableProperty]
    private bool _isToastVisible;

    [ObservableProperty]
    private string _toastMessage = "";

    [ObservableProperty]
    private string _toastGlyph = Glyphs.Success;

    [ObservableProperty]
    private Brush _toastAccent = CategoryColors.Brush("#16A34A");

    [ObservableProperty]
    private string? _toastActionText;

    private Func<Task>? _toastAction;
    private CancellationTokenSource? _toastCts;

    /// <summary>Mostra una notifica in basso a destra che sparisce da sola; può avere un pulsante (es. "Annulla").</summary>
    public void ShowToast(string message, ToastKind kind = ToastKind.Success, string? actionText = null, Func<Task>? action = null)
    {
        _toastCts?.Cancel();
        ToastMessage = message;
        (ToastGlyph, ToastAccent) = kind switch
        {
            ToastKind.Warning => (Glyphs.Warning, CategoryColors.Brush("#D97706")),
            ToastKind.Info => (Glyphs.Info, CategoryColors.Brush("#2563EB")),
            _ => (Glyphs.Success, CategoryColors.Brush("#16A34A")),
        };
        ToastActionText = actionText;
        _toastAction = action;
        IsToastVisible = true;

        var cts = _toastCts = new CancellationTokenSource();
        _ = Task.Delay(action is null ? 5000 : 10000, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                IsToastVisible = false;
        }, CancellationToken.None, TaskContinuationOptions.None,
           SynchronizationContext.Current is null ? TaskScheduler.Default : TaskScheduler.FromCurrentSynchronizationContext());
    }

    [RelayCommand]
    private void DismissToast()
    {
        _toastCts?.Cancel();
        IsToastVisible = false;
    }

    [RelayCommand]
    private async Task ToastActionAsync()
    {
        var action = _toastAction;
        DismissToast();
        if (action is not null)
            await action();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";
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
    [NotifyPropertyChangedFor(nameof(DetailsPanelVisible))]
    private FileRecord? _selectedFile;

    /// <summary>Vero quando il file selezionato è un'immagine: mostra il pannello anteprima a destra.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPanelVisible))]
    private bool _isPreviewVisible;

    [ObservableProperty]
    private ImageSource? _previewImage;

    [ObservableProperty]
    private string? _previewInfo;

    private CancellationTokenSource? _previewCts;

    partial void OnSelectedFileChanged(FileRecord? value) => Fire(LoadPreviewAsync(value), "anteprima");

    private async Task LoadPreviewAsync(FileRecord? file)
    {
        _previewCts?.Cancel();
        var visible = file is not null && (file.Category == FileCategory.Immagine || ImagePreview.CanPreview(file.Path));
        IsPreviewVisible = visible;
        PreviewImage = null;
        if (!visible)
            return;

        PreviewInfo = "Caricamento anteprima…";
        var cts = _previewCts = new CancellationTokenSource();
        try
        {
            var result = await ImagePreview.LoadAsync(file!.Path, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            PreviewImage = result.Image;
            PreviewInfo = result.Message
                ?? $"{result.PixelWidth} × {result.PixelHeight} pixel · {FileSizeConverter.Format(file.Size)}";
        }
        catch (OperationCanceledException)
        {
            // è stato selezionato un altro file
        }
    }

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
    private RootItem? _selectedRoot;

    [ObservableProperty]
    private CategoryItem? _selectedCategory;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(FullScanCommand), nameof(AddRootCommand), nameof(RemoveRootCommand),
        nameof(MoveSelectedCommand), nameof(CancelScanCommand), nameof(FindDuplicatesCommand),
        nameof(RecycleMarkedCommand), nameof(MoveMarkedCommand), nameof(RecycleSelectedCommand), nameof(OrganizeWithRulesCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "Pronto";

    partial void OnSearchTextChanged(string value)
    {
        Fire(RefreshResultsAsync(debounce: true), "ricerca");
        ProductsView.Refresh();
    }

    partial void OnSelectedCategoryChanged(CategoryItem? value) => Fire(RefreshResultsAsync(debounce: false), "ricerca");

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
            if (!string.Equals(root.Path, PcOrg.Core.PathUtil.Normalize(folder), StringComparison.OrdinalIgnoreCase))
                ShowToast($"Unità di rete salvata come {root.Path}: funzionerà anche se la lettera dell'unità cambia", ToastKind.Info);
            await ScanRootsAsync([root]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _dialogs.Error(ex.Message, "Cartella non aggiunta");
        }
    }

    /// <summary>Aggiunge una cartella condivisa in rete (NAS, altro PC) scrivendone il percorso.</summary>
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task AddNetworkRootAsync()
    {
        var path = _dialogs.AskNetworkFolder()?.Trim();
        if (string.IsNullOrEmpty(path))
            return;

        Status = $"Verifica di {path}…";
        if (!await NetworkPaths.IsReachableAsync(path, TimeSpan.FromSeconds(12)))
        {
            Status = "Pronto";
            _dialogs.Error(
                $"Non riesco a raggiungere \"{path}\".\n\n" +
                "• Controlla che il computer o il NAS sia acceso e collegato alla rete.\n" +
                "• Controlla il nome della condivisione (es. \\\\NAS\\Foto).\n" +
                "• Se servono nome utente e password, apri prima la cartella da Esplora risorse e accedi: pcOrg userà lo stesso accesso.",
                "Cartella di rete non raggiungibile");
            return;
        }
        try
        {
            var root = _index.AddRoot(path);
            ReloadRoots();
            ShowToast($"Cartella di rete aggiunta: {root.Path}", ToastKind.Success);
            await ScanRootsAsync([root]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _dialogs.Error(ex.Message, "Cartella non aggiunta");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveRoot))]
    private async Task RemoveRootAsync()
    {
        if (SelectedRoot?.Root is not { } root)
            return;
        if (!_dialogs.Confirm(
                $"pcOrg smetterà di tenere in ordine \"{root.Path}\".\n\nI file restano dove sono: vengono solo tolti dall'elenco.",
                "Rimuovere la cartella?", "Rimuovi"))
            return;
        _index.RemoveRoot(root.Id);
        ReloadRoots();
        await RefreshAsync();
        ShowToast($"Cartella rimossa: {root.Path}", ToastKind.Info);
    }

    private bool CanModify() => !IsBusy;
    private bool CanRemoveRoot() => !IsBusy && SelectedRoot is not null;

    /// <summary>Aggiornamento rapido (F5): rilegge solo le cartelle cambiate.</summary>
    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task ScanAsync() => ScanRootsAsync(Roots.Select(r => r.Root).ToList());

    /// <summary>Scansione completa (Ctrl+F5): rilegge tutte le cartelle, rileva anche i file modificati nel contenuto.</summary>
    [RelayCommand(CanExecute = nameof(CanModify))]
    private Task FullScanAsync() => ScanRootsAsync(Roots.Select(r => r.Root).ToList(), full: true);

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void CancelScan() => _scanCts?.Cancel();

    /// <summary>
    /// Aggiorna le cartelle indicate. Di norma è un aggiornamento rapido (<see cref="FileIndex.UpdateRoot"/>):
    /// si rileggono solo le cartelle in cui qualcosa è stato aggiunto, tolto o rinominato.
    /// </summary>
    private async Task ScanRootsAsync(IReadOnlyList<RootFolder> roots, bool full = false)
    {
        if (roots.Count == 0)
        {
            Status = "Aggiungi una cartella per iniziare.";
            return;
        }

        IsBusy = true;
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;
        var total = ScanResult.Empty;
        var offline = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        var kind = full ? "Scansione completa" : "Aggiornamento rapido";
        Log.Info($"{kind} avviato: {string.Join(", ", roots.Select(r => r.Path))}");
        try
        {
            foreach (var root in roots)
            {
                Status = $"Verifica di {root.Path}…";
                var timeout = root.IsNetwork ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(3);
                if (!await NetworkPaths.IsReachableAsync(root.Path, timeout))
                {
                    offline.Add(root.Path);
                    continue;
                }
                // Su FAT32/exFAT (chiavette) la data delle cartelle non segnala i file nuovi: scansione completa.
                var fullForRoot = full || !FileIndex.SupportsQuickUpdate(root.Path);
                if (fullForRoot && !full)
                    Log.Info($"{root.Path}: disco senza date di cartella affidabili (FAT/exFAT), scansione completa.");
                var progress = new Progress<ScanResult>(p => Status =
                    $"{kind} di {root.Path}: {p.DirectoriesVisited:N0} cartelle controllate, {p.DirectoriesChanged:N0} rilette, " +
                    $"{p.FilesChanged:N0} file cambiati…");
                try
                {
                    total += await Task.Run(
                        () => _index.UpdateRoot(root.Id, root.Path, fullForRoot, progress, token,
                            isStillAvailable: () => Directory.Exists(root.Path)), token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Es. la rete è caduta durante la scansione: i file già trovati sono salvati,
                    // quelli non ancora visti restano com'erano.
                    Log.Warn($"Scansione di {root.Path} non completata.", ex);
                    offline.Add(root.Path);
                }
            }
            var summary = DescribeChanges(total);
            Status = $"{kind} completato in {stopwatch.Elapsed.TotalSeconds:N1} s: {summary} " +
                     $"({total.DirectoriesVisited:N0} cartelle controllate, {total.DirectoriesChanged:N0} rilette)";
            Log.Info($"{kind} completato in {stopwatch.Elapsed.TotalSeconds:N1} s: {summary}; " +
                     $"cartelle controllate {total.DirectoriesVisited:N0}, rilette {total.DirectoriesChanged:N0}" +
                     (offline.Count > 0 ? $"; non raggiungibili: {string.Join(", ", offline)}" : ""));
            if (offline.Count == 0)
                ShowToast($"Indice aggiornato: {summary}", total.FilesChanged > 0 ? ToastKind.Success : ToastKind.Info);
            else
                ShowToast(offline.Count == 1
                        ? $"{offline[0]} non è raggiungibile: i suoi file restano nell'elenco e verranno aggiornati quando torna disponibile"
                        : $"{offline.Count} cartelle non raggiungibili: i loro file restano nell'elenco",
                    ToastKind.Warning);
        }
        catch (OperationCanceledException)
        {
            Status = "Scansione interrotta: i file trovati finora sono salvati.";
            Log.Info("Scansione interrotta dall'utente.");
            ShowToast("Scansione interrotta: i file trovati finora sono salvati e la prossima scansione riprenderà da lì", ToastKind.Warning);
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
        Fire(RefreshInstalledAsync(), "programmi installati");
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
            var count = await Task.Run(() => MetadataUpdater.Run(_index, progress, token, _settings.VerifySignatures), token);
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

    /// <summary>Testo come "3 file nuovi, 1 rimosso" o "nessuna modifica".</summary>
    private static string DescribeChanges(ScanResult r)
    {
        var parts = new List<string>();
        if (r.FilesAdded > 0) parts.Add(r.FilesAdded == 1 ? "1 file nuovo" : $"{r.FilesAdded:N0} file nuovi");
        if (r.FilesUpdated > 0) parts.Add(r.FilesUpdated == 1 ? "1 modificato" : $"{r.FilesUpdated:N0} modificati");
        if (r.FilesRemoved > 0) parts.Add(r.FilesRemoved == 1 ? "1 rimosso" : $"{r.FilesRemoved:N0} rimossi");
        return parts.Count == 0 ? "nessuna modifica" : string.Join(", ", parts);
    }

    private void ReloadRoots()
    {
        var selected = SelectedRoot?.Id;
        Roots.Clear();
        foreach (var root in _index.GetRoots())
            Roots.Add(new RootItem(root));
        SelectedRoot = Roots.FirstOrDefault(r => r.Id == selected);
        OnPropertyChanged(nameof(HasRoots));
        _watcher?.Watch(_index.GetRoots());
        Fire(CheckAvailabilityAsync(Roots.ToList()), "verifica delle cartelle");
    }

    /// <summary>Verifica in background quali cartelle (soprattutto di rete) sono raggiungibili.</summary>
    private static async Task CheckAvailabilityAsync(IReadOnlyList<RootItem> items)
    {
        await Task.WhenAll(items.Select(async item =>
        {
            var timeout = item.IsNetwork ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(3);
            item.IsAvailable = await NetworkPaths.IsReachableAsync(item.Path, timeout);
        }));
    }

    // ------------------------------------------------------------------ Categorie e ricerca

    /// <summary>Impedisce che due aggiornamenti delle categorie si sovrappongano mescolando i risultati.</summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>Ricarica categorie, prodotti e risultati dall'indice.</summary>
    private async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            await RefreshCoreAsync();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshCoreAsync()
    {
        var stats = await Task.Run(_index.GetCategoryStats);
        _lastStats = stats;
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
        UpdateDashboard();
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

    /// <summary>Ricarica l'elenco anche se i file sono gli stessi (es. è cambiato lo stato "Sul PC").</summary>
    private bool _forceResultsRefresh;

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

            // Se l'elenco non è cambiato non lo si ricarica: così un aggiornamento in background
            // (es. un file arrivato in un'altra categoria) non fa perdere selezione e posizione.
            var force = _forceResultsRefresh;
            _forceResultsRefresh = false;
            if (force || !Results.SequenceEqual(results))
            {
                Results.Clear();
                foreach (var file in results)
                    Results.Add(file);
            }
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
        if (runsCode && !_dialogs.Confirm(
                $"\"{file.Name}\" è un programma o un installer: verrà avviato.\n\nPercorso: {file.Path}",
                "Eseguire il file?", "Esegui"))
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
        if (SelectedFiles.Count == 0)
            return;
        Clipboard.SetText(string.Join(Environment.NewLine, SelectedFiles.Select(f => f.Path)));
        ShowToast(SelectedFiles.Count == 1 ? "Percorso copiato negli appunti" : $"{SelectedFiles.Count} percorsi copiati negli appunti", ToastKind.Info);
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task MoveSelectedAsync()
    {
        var files = SelectedFiles.ToList();
        if (files.Count == 0)
        {
            ShowToast("Seleziona prima uno o più file da spostare", ToastKind.Info);
            return;
        }

        var destination = _dialogs.PickFolder($"Dove vuoi spostare {files.Count} file?");
        if (destination is null)
            return;

        var plan = MovePlanner.Plan(files.Select(f => f.Path), destination);
        if (!plan.Any(p => p.WillMove))
        {
            ShowToast("Nessun file da spostare: sono già in quella cartella", ToastKind.Info);
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
            Log.Info($"Spostamento #{result.BatchId}: {result.Moved} file in {destination}, {result.Errors.Count} errori." +
                     (result.Errors.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, result.Errors) : ""));

            Status = $"Spostati {result.Moved:N0} file in {destination}";
            if (result.Moved > 0)
                ShowToast($"{result.Moved:N0} file spostati in {Path.GetFileName(destination.TrimEnd('\\'))}",
                    ToastKind.Success, "Annulla", () => UndoMoveAsync(result.BatchId));
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

    private async Task UndoMoveAsync(long batchId)
    {
        var result = await Task.Run(() => _moves.Undo(batchId));
        Log.Info($"Annullato lo spostamento #{batchId}: {result.Moved} file ripristinati, {result.Errors.Count} errori.");
        await RefreshAsync();
        if (result.Errors.Count == 0)
            ShowToast($"Spostamento annullato: {result.Moved:N0} file tornati al loro posto", ToastKind.Info);
        else
            _dialogs.Error($"Ripristinati {result.Moved} file. {result.Errors.Count} non sono stati ripristinati:\n\n"
                + string.Join("\n", result.Errors.Take(15)), "Annullamento incompleto");
    }

    [RelayCommand]
    private void ShowHistory() => _dialogs.ShowMoveHistory(_index, () => Fire(RefreshAsync(), "aggiornamento"));
}
