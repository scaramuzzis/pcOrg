using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PcOrg.Core;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Metadata;
using PcOrg.Core.Storage;

namespace PcOrg.App.ViewModels;

/// <summary>
/// Barra di avanzamento comune a tutte le operazioni lunghe (scansioni, versioni e firme, contenuto dei
/// documenti, cloni, spostamenti, download): percentuale quando il totale è noto, animata altrimenti.
/// Qui anche la lettura "su richiesta" dei dettagli del file selezionato.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Avanzamento da 0 a 100 (vale quando <see cref="ProgressIndeterminate"/> è falso).</summary>
    [ObservableProperty]
    private double _progressPercent;

    /// <summary>Vero quando il totale non si conosce (es. prima scansione di una cartella).</summary>
    [ObservableProperty]
    private bool _progressIndeterminate = true;

    /// <summary>Fase e percentuale, es. "Versioni e firme 42%".</summary>
    [ObservableProperty]
    private string _progressText = "";

    /// <summary>Avanzamento con conteggio: totale 0 = sconosciuto.</summary>
    private void ReportProgress(string phase, long done, long total)
    {
        if (total > 0)
            ReportProgress(phase, (double)done / total);
        else
        {
            ProgressIndeterminate = true;
            ProgressText = phase;
        }
    }

    private void ReportProgress(string phase, double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        ProgressIndeterminate = false;
        ProgressPercent = fraction * 100;
        ProgressText = $"{phase} {fraction:P0}";
    }

    private void ResetProgress()
    {
        ProgressIndeterminate = true;
        ProgressPercent = 0;
        ProgressText = "";
    }

    partial void OnIsBusyChanged(bool value)
    {
        if (!IsWorking)
            ResetProgress();
    }

    partial void OnIsScanningChanged(bool value)
    {
        if (!IsWorking)
            ResetProgress();
    }

    // ------------------------------------------------------------------ Dettagli letti su richiesta

    /// <summary>File di cui si è già tentata la lettura su richiesta in questa sessione (anche se non leggibili).</summary>
    private readonly HashSet<long> _onDemandTried = [];

    /// <summary>
    /// Se il file selezionato non ha ancora versione, produttore e firma (es. cartella di rete impostata
    /// "solo su richiesta", o lettura in background non ancora arrivata a lui), li legge subito: un solo file,
    /// poche decine di KB anche in rete.
    /// </summary>
    private async Task LoadDetailsOnDemandAsync(FileRecord? file)
    {
        if (file is null || file.Meta is not null || !MetadataExtractor.IsSupported(file.Path) || !_onDemandTried.Add(file.Id))
            return;
        var verify = _settings.VerifySignatures && !NetworkPaths.IsNetworkPath(file.Path);
        var pending = new PendingMetadata(file.Id, file.Path, file.Size, new DateTimeOffset(DateTime.SpecifyKind(file.ModifiedUtc, DateTimeKind.Utc)).ToUnixTimeSeconds());
        FileMeta? meta;
        try
        {
            meta = await Task.Run(() =>
            {
                var read = PcOrg.Core.Drives.DiskActivity.Measure(file.Path, directory: false, () => MetadataExtractor.Extract(file.Path, verify));
                _index.SaveMetadata([(pending, read)]);
                return read;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log.Warn($"Dettagli di {file.Path} non letti.", ex);
            return;
        }
        if (meta is null)
            return;
        Log.Debug($"Dettagli letti su richiesta: {file.Path}");
        _forceResultsRefresh = true; // le colonne dell'elenco si aggiornano al prossimo ridisegno
        if (SelectedFile?.Id == file.Id)
            SelectedFile = file with { Meta = meta };
    }
}
