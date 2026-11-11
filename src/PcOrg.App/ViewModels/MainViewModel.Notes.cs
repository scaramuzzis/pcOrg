using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcOrg.App.Views;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Updates;

namespace PcOrg.App.ViewModels;

/// <summary>Ricerca nel contenuto, etichette e note, controllo degli aggiornamenti di pcOrg.</summary>
public sealed partial class MainViewModel
{
    /// <summary>La ricerca guarda anche dentro i file (testo dei documenti, contenuto di ZIP e ISO).</summary>
    [ObservableProperty]
    private bool _searchInContent;

    partial void OnSearchInContentChanged(bool value)
    {
        _settings.SearchInContent = value;
        SaveSettings();
        Fire(RefreshResultsAsync(debounce: false), "ricerca");
    }

    /// <summary>Assegna etichette e nota ai file selezionati (Ctrl+T).</summary>
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task EditNotesAsync()
    {
        var files = SelectedFiles.ToList();
        if (files.Count == 0)
        {
            ShowToast("Seleziona prima uno o più file", ToastKind.Info);
            return;
        }
        // Con più file si parte dai valori comuni: se differiscono il campo parte vuoto.
        var tags = files.Select(f => f.Tags ?? "").Distinct().Count() == 1 ? files[0].Tags ?? "" : "";
        var note = files.Select(f => f.Note ?? "").Distinct().Count() == 1 ? files[0].Note ?? "" : "";
        var known = await Task.Run(() => _index.GetTags().Select(t => t.Tag).ToList());
        var title = files.Count == 1 ? files[0].Name : $"{files.Count:N0} file selezionati";
        if (_dialogs.EditNotes(title, tags, note, known) is not { } result)
            return;

        await Task.Run(() => _index.SetNotes(files.Select(f => f.Path), result.Tags, result.Note));
        Log.Info($"Etichette e note aggiornate per {files.Count} file.");
        _forceResultsRefresh = true;
        await RefreshResultsAsync(debounce: false);
        ShowToast(files.Count == 1 ? "Etichette e nota salvate" : $"Etichette e nota salvate per {files.Count:N0} file", ToastKind.Success);
    }

    /// <summary>Controlla (su richiesta) se è stata pubblicata una versione più recente di pcOrg.</summary>
    [RelayCommand]
    private Task CheckUpdates() => CheckUpdatesAsync(silentIfCurrent: false);

    private async Task CheckUpdatesAsync(bool silentIfCurrent)
    {
        var info = await UpdateChecker.CheckAsync(AboutWindow.AppVersion);
        Log.Info($"Controllo aggiornamenti: {info.Status} (ultima versione {info.LatestVersion ?? "sconosciuta"}).");
        switch (info.Status)
        {
            case UpdateStatus.NuovaVersione:
                ShowToast($"È disponibile pcOrg {info.LatestVersion} (hai la {AboutWindow.AppVersion})", ToastKind.Success, "Apri la pagina",
                    () =>
                    {
                        Process.Start(new ProcessStartInfo(info.Url ?? UpdateChecker.ReleasesPage) { UseShellExecute = true });
                        return Task.CompletedTask;
                    });
                break;
            case UpdateStatus.Aggiornato when !silentIfCurrent:
                ShowToast($"Hai già la versione più recente ({AboutWindow.AppVersion})", ToastKind.Info);
                break;
            case UpdateStatus.NonDisponibile when !silentIfCurrent:
                ShowToast($"Non è stato possibile controllare gli aggiornamenti. {info.Error}", ToastKind.Warning, "Apri la pagina",
                    () =>
                    {
                        Process.Start(new ProcessStartInfo(UpdateChecker.ReleasesPage) { UseShellExecute = true });
                        return Task.CompletedTask;
                    });
                break;
        }
    }
}
