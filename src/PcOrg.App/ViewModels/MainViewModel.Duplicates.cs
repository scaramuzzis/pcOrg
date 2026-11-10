using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcOrg.App.Infrastructure;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Duplicates;
using PcOrg.Core.Moving;
using PcOrg.Core.Storage;

namespace PcOrg.App.ViewModels;

/// <summary>Una copia dentro un gruppo di cloni, con il segno "da eliminare".</summary>
public sealed partial class DuplicateFileItem(FileRecord file, bool isKeeper, Action<DuplicateFileItem> onMarkChanged) : ObservableObject
{
    public FileRecord File { get; } = file;

    /// <summary>La copia che la regola scelta suggerisce di tenere.</summary>
    public bool IsKeeper { get; } = isKeeper;

    public string State => IsKeeper ? "Da tenere" : "Copia";

    [ObservableProperty]
    private bool _isMarked;

    partial void OnIsMarkedChanged(bool value) => onMarkChanged(this);
}

/// <summary>Opzione di dimensione minima per la ricerca dei cloni.</summary>
public sealed record SizeOption(string Label, long Bytes);

/// <summary>Regola "quale copia tenere" con il testo da mostrare.</summary>
public sealed record KeepRuleOption(KeepRule Rule, string Label);

/// <summary>Scheda "Cloni": ricerca dei file identici, segni e pulizia (Cestino o spostamento).</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<DuplicateGroup> DuplicateGroups { get; } = [];
    public ObservableCollection<DuplicateFileItem> DuplicateFiles { get; } = [];

    /// <summary>Id dei file segnati come doppioni da eliminare (in tutti i gruppi).</summary>
    private readonly HashSet<long> _marked = [];

    public IReadOnlyList<SizeOption> SizeOptions { get; } =
    [
        new("Tutti i file", 1),
        new("Più grandi di 1 MB", 1L << 20),
        new("Più grandi di 10 MB", 10L << 20),
        new("Più grandi di 100 MB", 100L << 20),
    ];

    public IReadOnlyList<KeepRuleOption> KeepRules { get; } =
        Enum.GetValues<KeepRule>().Select(r => new KeepRuleOption(r, CleanupRules.Describe(r))).ToList();

    [ObservableProperty]
    private SizeOption? _selectedSizeOption;

    [ObservableProperty]
    private bool _includeNetworkInDuplicates;

    [ObservableProperty]
    private KeepRuleOption? _selectedKeepRule;

    [ObservableProperty]
    private DuplicateGroup? _selectedDuplicateGroup;

    [ObservableProperty]
    private string _duplicatesSummary = "Premi \"Cerca i cloni\" per trovare i file identici nelle cartelle monitorate.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecycleMarkedCommand), nameof(MoveMarkedCommand))]
    private int _markedCount;

    [ObservableProperty]
    private string _markedSummary = "Nessun file segnato";

    private KeepRule CurrentRule => SelectedKeepRule?.Rule ?? KeepRule.PiuVecchio;

    partial void OnSelectedDuplicateGroupChanged(DuplicateGroup? value) => ShowGroupFiles(value);

    partial void OnSelectedKeepRuleChanged(KeepRuleOption? value) => ShowGroupFiles(SelectedDuplicateGroup);

    private void ShowGroupFiles(DuplicateGroup? group)
    {
        DuplicateFiles.Clear();
        if (group is null)
            return;
        var keeper = CleanupRules.Keeper(group, CurrentRule);
        foreach (var file in group.Files)
            DuplicateFiles.Add(new DuplicateFileItem(file, file.Id == keeper.Id, OnMarkChanged) { IsMarked = _marked.Contains(file.Id) });
    }

    private void OnMarkChanged(DuplicateFileItem item)
    {
        if (item.IsMarked)
            _marked.Add(item.File.Id);
        else
            _marked.Remove(item.File.Id);
        UpdateMarkedSummary();
    }

    private void UpdateMarkedSummary()
    {
        var files = DuplicateGroups.SelectMany(g => g.Files).Where(f => _marked.Contains(f.Id)).ToList();
        MarkedCount = files.Count;
        MarkedSummary = files.Count == 0 ? "Nessun file segnato"
            : $"Segnati {files.Count:N0} file · {FileSizeConverter.Format(files.Sum(f => f.Size))}";
    }

    /// <summary>Cerca i cloni nelle cartelle monitorate (in background, interrompibile).</summary>
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task FindDuplicatesAsync()
    {
        IsBusy = true;
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var options = new DuplicateOptions(SelectedSizeOption?.Bytes ?? 1, IncludeNetworkInDuplicates);
            var progress = new Progress<DuplicateProgress>(p =>
                Status = p.Total == 0 ? $"{p.Phase}…" : $"{p.Phase}: {p.Done:N0} di {p.Total:N0} file…");
            var groups = await Task.Run(() => DuplicateFinder.Find(_index, options, progress, token), token);
            ShowDuplicateGroups(groups);
            var wasted = groups.Sum(g => g.WastedBytes);
            Status = $"Ricerca dei cloni completata in {stopwatch.Elapsed.TotalSeconds:N1} s";
            ShowToast(groups.Count == 0
                ? "Nessun file identico trovato"
                : $"{groups.Count:N0} gruppi di file identici: si possono liberare {FileSizeConverter.Format(wasted)}",
                groups.Count == 0 ? ToastKind.Info : ToastKind.Success);
        }
        catch (OperationCanceledException)
        {
            Status = "Ricerca dei cloni interrotta: le impronte calcolate sono salvate e verranno riusate.";
        }
        finally
        {
            IsBusy = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private void ShowDuplicateGroups(IReadOnlyList<DuplicateGroup> groups)
    {
        var selected = SelectedDuplicateGroup?.Hash;
        DuplicateGroups.Clear();
        foreach (var group in groups)
            DuplicateGroups.Add(group);
        var ids = groups.SelectMany(g => g.Files).Select(f => f.Id).ToHashSet();
        _marked.IntersectWith(ids);
        SelectedDuplicateGroup = DuplicateGroups.FirstOrDefault(g => g.Hash == selected) ?? DuplicateGroups.FirstOrDefault();
        var wasted = groups.Sum(g => g.WastedBytes);
        DuplicatesSummary = groups.Count == 0
            ? "Nessun file identico nelle cartelle monitorate."
            : $"{groups.Count:N0} gruppi di file identici, {groups.Sum(g => g.Files.Count):N0} file in tutto. " +
              $"Tenendo una sola copia di ciascuno si liberano {FileSizeConverter.Format(wasted)}.";
        UpdateMarkedSummary();
    }

    /// <summary>Segna in tutti i gruppi le copie da eliminare, tenendone una secondo la regola scelta.</summary>
    [RelayCommand]
    private void MarkDoubles()
    {
        _marked.Clear();
        foreach (var file in CleanupRules.Doubles(DuplicateGroups, CurrentRule))
            _marked.Add(file.Id);
        ShowGroupFiles(SelectedDuplicateGroup);
        UpdateMarkedSummary();
    }

    [RelayCommand]
    private void ClearMarks()
    {
        _marked.Clear();
        ShowGroupFiles(SelectedDuplicateGroup);
        UpdateMarkedSummary();
    }

    private bool HasMarked() => MarkedCount > 0 && !IsBusy;

    private List<FileRecord> MarkedFiles() =>
        DuplicateGroups.SelectMany(g => g.Files).Where(f => _marked.Contains(f.Id)).DistinctBy(f => f.Id).ToList();

    /// <summary>Sposta nel Cestino le copie segnate, controllando che di ogni file resti almeno una copia.</summary>
    [RelayCommand(CanExecute = nameof(HasMarked))]
    private async Task RecycleMarkedAsync()
    {
        var losing = CleanupRules.GroupsLosingAllCopies(DuplicateGroups, _marked);
        if (losing.Count > 0)
        {
            _dialogs.Error(
                $"Hai segnato tutte le copie di {losing.Count} file (es. \"{losing[0].Name}\"): così il file andrebbe perso.\n\n" +
                "Togli il segno da almeno una copia per ogni gruppo, oppure usa \"Segna i doppioni\".",
                "Resterebbe senza nessuna copia");
            return;
        }
        var files = MarkedFiles();
        if (await RecycleAsync(files))
        {
            var recycled = files.Select(f => f.Id).ToHashSet();
            _marked.ExceptWith(recycled);
            var remaining = DuplicateGroups
                .Select(g => g with { Files = g.Files.Where(f => !recycled.Contains(f.Id)).ToList() })
                .Where(g => g.Files.Count > 1)
                .ToList();
            ShowDuplicateGroups(remaining);
        }
    }

    /// <summary>Sposta le copie segnate in un'altra cartella (con anteprima e annullamento).</summary>
    [RelayCommand(CanExecute = nameof(HasMarked))]
    private async Task MoveMarkedAsync()
    {
        SelectedFiles = MarkedFiles();
        await MoveSelectedCommand.ExecuteAsync(null);
    }

    // ------------------------------------------------------------------ Cestino (anche per File e Prodotti)

    /// <summary>Sposta nel Cestino i file selezionati nell'elenco attivo (tasto Canc).</summary>
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task RecycleSelectedAsync()
    {
        var files = SelectedFiles.ToList();
        if (files.Count == 0)
        {
            ShowToast("Seleziona prima uno o più file", ToastKind.Info);
            return;
        }
        await RecycleAsync(files);
    }

    /// <summary>Chiede conferma e sposta i file nel Cestino di Windows; restituisce true se l'operazione è avvenuta.</summary>
    private async Task<bool> RecycleAsync(IReadOnlyList<FileRecord> files)
    {
        var bytes = files.Sum(f => f.Size);
        var list = string.Join("\n", files.Take(8).Select(f => "• " + f.Name)) + (files.Count > 8 ? $"\n• … e altri {files.Count - 8}" : "");
        if (!_dialogs.Confirm(
                $"{files.Count:N0} file ({FileSizeConverter.Format(bytes)}) verranno spostati nel Cestino di Windows. " +
                $"Potrai ripristinarli dal Cestino se servissero.\n\n{list}",
                "Spostare nel Cestino?", "Sposta nel Cestino"))
            return false;

        IsBusy = true;
        try
        {
            Status = "Spostamento nel Cestino…";
            var result = await Task.Run(() => RecycleBin.Recycle(_index, files));
            Status = $"Spostati nel Cestino {result.Recycled.Count:N0} file ({FileSizeConverter.Format(result.Bytes)})";
            if (result.Recycled.Count > 0)
                ShowToast($"{result.Recycled.Count:N0} file nel Cestino: liberati {FileSizeConverter.Format(result.Bytes)}. " +
                          "Puoi ripristinarli dal Cestino di Windows.", ToastKind.Success);
            if (result.Errors.Count > 0)
                _dialogs.Error($"{result.Errors.Count} file non sono stati spostati nel Cestino:\n\n" +
                               string.Join("\n", result.Errors.Take(15)), "Operazione incompleta");
            return result.Recycled.Count > 0;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Spostamento nel Cestino non riuscito.", ex);
            _dialogs.Error(ex.Message, "Cestino non disponibile");
            return false;
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }
}
