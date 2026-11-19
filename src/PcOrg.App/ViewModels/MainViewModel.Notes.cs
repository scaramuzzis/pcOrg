using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PcOrg.App.Infrastructure;
using PcOrg.App.Views;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Updates;

namespace PcOrg.App.ViewModels;

/// <summary>Ricerca nel contenuto, etichette e note, controllo degli aggiornamenti di Gestione File.</summary>
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

    /// <summary>Controlla (su richiesta) se è stata pubblicata una versione più recente di Gestione File.</summary>
    [RelayCommand]
    private Task CheckUpdates() => CheckUpdatesAsync(silentIfCurrent: false);

    private async Task CheckUpdatesAsync(bool silentIfCurrent)
    {
        var info = await UpdateChecker.CheckAsync(AboutWindow.AppVersion);
        Log.Info($"Controllo aggiornamenti: {info.Status} (ultima versione {info.LatestVersion ?? "sconosciuta"}).");
        switch (info.Status)
        {
            case UpdateStatus.NuovaVersione:
                var kind = CurrentInstallKind();
                var asset = AppUpdater.SelectAsset(info, kind);
                if (asset is not null)
                    ShowToast($"È disponibile Gestione File {info.LatestVersion} (hai la {AboutWindow.AppVersion})", ToastKind.Success, "Aggiorna ora",
                        () => InstallUpdateAsync(info, asset, kind));
                else
                    ShowToast($"È disponibile Gestione File {info.LatestVersion} (hai la {AboutWindow.AppVersion})", ToastKind.Success, "Apri la pagina",
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

    /// <summary>Tipo di installazione in uso (portatile, leggera, file unico, installata con MSI).</summary>
    private static InstallKind CurrentInstallKind() =>
        // Nel file unico le librerie sono dentro l'eseguibile: accanto non c'è GestioneFile.dll.
        AppUpdater.DetectKind(AppContext.BaseDirectory, !File.Exists(Path.Combine(AppContext.BaseDirectory, "GestioneFile.dll")));

    /// <summary>
    /// Aggiornamento automatico: con conferma scarica il pacchetto adatto a questa installazione,
    /// ne verifica l'impronta, poi chiude Gestione File; uno script sostituisce i file e la riapre.
    /// </summary>
    private async Task InstallUpdateAsync(UpdateInfo info, ReleaseAsset asset, InstallKind kind)
    {
        if (IsWorking)
        {
            ShowToast("Attendi la fine dell'operazione in corso, poi riprova ad aggiornare.", ToastKind.Info);
            return;
        }
        var sizeText = asset.Size > 0 ? $" ({FileSizeConverter.Format(asset.Size)})" : "";
        if (!_dialogs.Confirm(
                $"Gestione File verrà aggiornato alla versione {info.LatestVersion} ({AppUpdater.Describe(kind)}).\n\n" +
                $"Il pacchetto {asset.Name}{sizeText} viene scaricato da GitHub e controllato; poi l'app si chiude, " +
                "i file del programma vengono sostituiti e Gestione File si riapre da solo.\n\n" +
                "Indice, impostazioni e cartelle monitorate restano come sono.",
                "Aggiornare Gestione File?", "Aggiorna"))
            return;

        IsBusy = true;
        PreparedUpdate prepared;
        var folder = Path.Combine(Path.GetTempPath(), "GestioneFile-aggiornamento");
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            var progress = new Progress<double>(p => Status = $"Download dell'aggiornamento {info.LatestVersion}: {p:P0}");
            var file = await AppUpdater.DownloadAsync(asset, folder, AboutWindow.AppVersion, progress);
            Status = "Preparazione dell'aggiornamento…";
            prepared = await Task.Run(() => AppUpdater.Prepare(kind, file, info.LatestVersion!));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException
                                       or TaskCanceledException or InvalidDataException)
        {
            Log.Warn("Aggiornamento automatico non riuscito.", ex);
            Status = "Aggiornamento non riuscito";
            ShowToast($"Aggiornamento non riuscito: {ex.Message}", ToastKind.Warning, "Apri la pagina", () =>
            {
                Process.Start(new ProcessStartInfo(info.Url ?? UpdateChecker.ReleasesPage) { UseShellExecute = true });
                return Task.CompletedTask;
            });
            return;
        }
        finally
        {
            IsBusy = false;
        }

        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AppUpdater.ExeName);
        var script = Path.Combine(folder, "aggiorna.ps1");
        var logFile = Path.Combine(AppPaths.LogFolder, "pcorg.log");
        await File.WriteAllTextAsync(script,
            AppUpdater.BuildScript(prepared, AppContext.BaseDirectory.TrimEnd('\\', '/'), exe, Environment.ProcessId, logFile),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Log.Info($"Aggiornamento alla versione {prepared.Version} pronto ({kind}): chiusura e sostituzione dei file.");
        Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Application.Current.MainWindow?.Close();
    }
}
