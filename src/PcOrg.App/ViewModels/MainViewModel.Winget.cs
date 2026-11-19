using System.IO;
using CommunityToolkit.Mvvm.Input;
using PcOrg.App.Infrastructure;
using PcOrg.App.Views;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Metadata;
using PcOrg.Core.Updates;

namespace PcOrg.App.ViewModels;

/// <summary>
/// Versioni più recenti dei programmi: confronto dei prodotti trovati sul PC con il catalogo pubblico
/// di winget. Parte solo su richiesta; il catalogo scaricato resta valido per un giorno.
/// </summary>
public sealed partial class MainViewModel
{
    private WingetCatalog? _winget;

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task CheckProgramUpdates()
    {
        IsBusy = true;
        try
        {
            Status = "Download del catalogo di winget…";
            var progress = new Progress<double>(p => Status = $"Download del catalogo di winget: {p:P0}");
            var file = await WingetCatalog.EnsureIndexAsync(Path.Combine(AppPaths.DataFolder, "winget"), AboutWindow.AppVersion, progress);
            Status = "Confronto con i programmi trovati…";
            _winget = await Task.Run(() => WingetCatalog.Load(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException
                                       or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or TaskCanceledException)
        {
            Log.Warn("Controllo delle versioni dei programmi non riuscito.", ex);
            Status = "Catalogo di winget non disponibile";
            ShowToast("Non è stato possibile scaricare il catalogo di winget. Controlla la connessione e riprova.", ToastKind.Warning);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        ApplyWinget(Products);
        var found = InstalledCatalog.Online.Count;
        var newer = InstalledCatalog.Online.Values.Count(m => m.IsNewer);
        Status = $"Catalogo di winget: {_winget.Count:N0} programmi; riconosciuti {found:N0} dei tuoi prodotti.";
        ProductsView.Refresh();
        ShowToast(found == 0
                ? "Nessuno dei tuoi programmi è stato trovato nel catalogo di winget."
                : newer == 0
                    ? $"Hai le ultime versioni di tutti i {found:N0} programmi riconosciuti."
                    : $"{newer:N0} programmi su {found:N0} hanno una versione più recente: vedi la colonna Online in Prodotti e versioni.",
            newer > 0 ? ToastKind.Info : ToastKind.Success, newer > 0 ? "Mostra" : null,
            newer > 0 ? () => { SelectedTabIndex = 2; return Task.CompletedTask; } : null);
        Log.Info($"Versioni dei programmi: {found} riconosciuti, {newer} con versione più recente su winget.");
    }

    /// <summary>Ricalcola la colonna Online per i prodotti in elenco (se il catalogo è già stato scaricato).</summary>
    private void ApplyWinget(IEnumerable<ProductGroup> groups)
    {
        if (_winget is null)
            return;
        var map = new Dictionary<string, WingetMatch>();
        foreach (var g in groups)
            if (_winget.Match(g.Name, g.Company, g.LatestVersion) is { } match)
                map[g.Key] = match;
        InstalledCatalog.Online = map;
    }
}
