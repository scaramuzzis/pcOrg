using System.IO;
using CommunityToolkit.Mvvm.Input;
using PcOrg.App.Infrastructure;
using PcOrg.Core;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Installed;
using PcOrg.Core.Rules;
using PcOrg.Core.Storage;

namespace PcOrg.App.ViewModels;

/// <summary>Regole di archiviazione ("Organizza con le regole") e confronto con i programmi installati.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Regole in uso (salvate nelle preferenze).</summary>
    public List<ArchiveRule> Rules => _settings.Rules;

    /// <summary>Sostituisce l'elenco delle regole (dalla finestra Impostazioni) e lo salva.</summary>
    public void SetRules(IEnumerable<ArchiveRule> rules)
    {
        _settings.Rules = rules.ToList();
        SaveSettings();
        Log.Info($"Regole di archiviazione salvate: {_settings.Rules.Count} ({_settings.Rules.Count(r => r.Enabled)} attive).");
    }

    /// <summary>
    /// Applica le regole ai file selezionati (o, se non c'è selezione, a tutti i file della vista attuale)
    /// e mostra l'anteprima: nulla viene spostato senza conferma, e tutto si può annullare.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task OrganizeWithRulesAsync()
    {
        var rules = _settings.Rules.Where(r => r.Enabled).ToList();
        if (rules.Count == 0)
        {
            ShowToast("Non ci sono regole attive: creale in Strumenti → Impostazioni → Regole", ToastKind.Info,
                "Crea regole", () => { RequestOpenSettings?.Invoke(RulesTabIndex); return Task.CompletedTask; });
            return;
        }

        var files = SelectedFiles.Count > 1
            ? SelectedFiles.ToList()
            : (await Task.Run(() => _index.Search(new SearchQuery(SearchText, SelectedCategory?.Category, Limit: 1_000_000)
            {
                Extensions = SelectedCategory?.Extensions,
            }))).ToList();
        var (plan, unmatched) = await Task.Run(() => RuleEngine.Plan(files, rules));
        if (!plan.Any(p => p.WillMove))
        {
            ShowToast(plan.Count == 0
                ? $"Nessuno dei {files.Count:N0} file corrisponde alle regole attive"
                : "I file sono già nelle cartelle indicate dalle regole", ToastKind.Info);
            return;
        }

        // Le cartelle di base delle regole (la parte prima dei segnaposto) dovrebbero essere monitorate,
        // altrimenti i file archiviati non sarebbero più cercabili.
        var bases = rules.Select(BaseFolder).Where(b => b is not null).Select(b => b!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var allMonitored = bases.All(b => _index.FindRootFor(b) is not null);
        var toMove = plan.Count(p => p.WillMove);
        var folders = plan.Where(p => p.WillMove).Select(p => Path.GetDirectoryName(p.Destination)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var summary = $"Le regole spostano {toMove:N0} file in {folders:N0} cartelle." +
                      (unmatched > 0 ? $" {unmatched:N0} file non corrispondono a nessuna regola e restano dove sono." : "");
        if (_dialogs.PreviewMove(plan, string.Join(", ", bases), allMonitored, summary) is not { } choice)
            return;

        IsBusy = true;
        try
        {
            if (!allMonitored && choice.AddDestinationToMonitored)
            {
                foreach (var folder in bases.Where(b => _index.FindRootFor(b) is null))
                {
                    try
                    {
                        Directory.CreateDirectory(folder);
                        _index.AddRoot(folder);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                    {
                        Log.Warn($"Cartella di archivio non aggiunta: {folder}", ex);
                    }
                }
                ReloadRoots();
            }
            var progress = new Progress<int>(n => Status = $"Organizzazione: {n:N0} di {toMove:N0} file…");
            var result = await Task.Run(() => _moves.Execute(plan, $"Organizzazione con le regole: {toMove} file", progress));
            Log.Info($"Organizzazione con le regole #{result.BatchId}: {result.Moved} file spostati, {result.Errors.Count} errori.");
            ShowToast($"{result.Moved:N0} file archiviati con le regole", ToastKind.Success, "Annulla", () => UndoMoveAsync(result.BatchId));
            if (result.Errors.Count > 0)
                _dialogs.Error($"{result.Errors.Count} file non sono stati spostati:\n\n" + string.Join("\n", result.Errors.Take(15)),
                    "Organizzazione incompleta");
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    /// <summary>Indice della scheda Regole nella finestra Impostazioni.</summary>
    public const int RulesTabIndex = 2;

    /// <summary>La finestra principale apre le Impostazioni quando il ViewModel lo chiede (es. dalla notifica).</summary>
    public event Action<int>? RequestOpenSettings;

    /// <summary>Cartella fissa di una regola, cioè il percorso prima del primo segnaposto.</summary>
    public static string? BaseFolder(ArchiveRule rule)
    {
        var dest = rule.Destination.Trim();
        var brace = dest.IndexOf('{');
        var fixedPart = brace < 0 ? dest : dest[..brace];
        fixedPart = fixedPart.TrimEnd('\\', '/');
        if (brace >= 0 && !string.IsNullOrEmpty(fixedPart) && !dest[..brace].EndsWith('\\'))
            fixedPart = Path.GetDirectoryName(fixedPart) ?? fixedPart; // es. "D:\Arch{Anno}" → "D:\"
        return string.IsNullOrWhiteSpace(fixedPart) ? null : PathUtil.Normalize(fixedPart);
    }

    // ------------------------------------------------------------------ Programmi installati

    /// <summary>Legge in background i programmi installati e aggiorna le colonne "Sul PC".</summary>
    public async Task RefreshInstalledAsync()
    {
        var programs = await Task.Run(InstalledPrograms.Read);
        InstalledCatalog.Current = new InstalledIndex(programs);
        await RefreshAsync(); // ridisegna gli elenchi con lo stato aggiornato
    }
}
