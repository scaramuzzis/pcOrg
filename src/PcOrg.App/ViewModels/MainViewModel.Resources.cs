using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Storage;

namespace PcOrg.App.ViewModels;

/// <summary>Una voce del riquadro "Risorse della scansione" (es. "Traffico di rete" → "12,4 MB").</summary>
public sealed record ResourceItem(string Label, string Value, string Hint);

/// <summary>
/// Risorse della scansione: mentre Gestione File aggiorna l'indice misura ogni secondo traffico di rete,
/// letture al secondo, dati letti, processore e memoria, e stima il tempo rimanente. Il riepilogo compare
/// nella barra di stato, nella scheda Dischi e, a fine scansione, nel registro attività.
/// </summary>
public sealed partial class MainViewModel
{
    private ResourceMonitor? _resourceMonitor;
    private DispatcherTimer? _resourceTimer;
    private string _resourceWhat = "";

    /// <summary>Riga compatta per la barra di stato (null quando non c'è una scansione in corso).</summary>
    [ObservableProperty]
    private string? _scanResourcesText;

    /// <summary>Titolo del riquadro nella scheda Dischi.</summary>
    [ObservableProperty]
    private string _scanResourcesTitle = "Nessuna scansione da quando Gestione File è aperto.";

    public ObservableCollection<ResourceItem> ScanResources { get; } = [];

    /// <summary>Inizia a misurare le risorse (se non lo si sta già facendo per la scansione in corso).</summary>
    private async Task<bool> StartResourceMonitorAsync(IReadOnlyList<RootFolder> roots, string what)
    {
        if (_resourceMonitor is not null)
            return false;
        // Cartelle già note: permettono di stimare quanto manca (alla prima scansione non si può sapere).
        var known = await Task.Run(() => roots.Select(r => _index.CountDirectories(r.Id)).ToList());
        _resourceMonitor = new ResourceMonitor(PcOrg.Core.Platform.PlatformServices.Current.ProcessIoCounters)
        {
            ExpectedWork = known.Any(k => k == 0) ? 0 : known.Sum(),
        };
        _resourceWhat = what;
        ScanResourcesTitle = $"In corso: {what}";
        _resourceTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _resourceTimer.Tick += (_, _) => UpdateResources(final: false);
        _resourceTimer.Start();
        return true;
    }

    /// <summary>Cartelle controllate finora (per la stima del tempo rimanente).</summary>
    private void ReportScanWork(long directoriesVisited)
    {
        if (_resourceMonitor is { } monitor)
            monitor.DoneWork = directoriesVisited;
    }

    private void StopResourceMonitor()
    {
        if (_resourceMonitor is null)
            return;
        _resourceTimer?.Stop();
        _resourceTimer = null;
        var sample = UpdateResources(final: true);
        _resourceMonitor = null;
        ScanResourcesText = null;
        if (sample is not null)
        {
            ScanResourcesTitle = $"Ultima scansione ({_resourceWhat}), terminata alle {DateTime.Now:HH:mm}";
            Log.Info($"Risorse della scansione ({_resourceWhat}): {sample.Summary}.");
        }
    }

    private ResourceSample? UpdateResources(bool final)
    {
        if (_resourceMonitor is not { } monitor)
            return null;
        ResourceSample sample;
        try
        {
            sample = monitor.Sample();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("Misura delle risorse non riuscita.", ex);
            return null;
        }
        if (!final)
            ScanResourcesText = sample.ShortText;

        var operationsPerSecond = sample.Operations / Math.Max(1, sample.Elapsed.TotalSeconds);
        var items = new List<ResourceItem>
        {
            new("Durata", ResourceSample.FormatDuration(sample.Elapsed),
                sample.Remaining is { } r ? $"mancano circa {ResourceSample.FormatDuration(r)}" : final ? "completata" : "tempo rimanente non stimabile alla prima scansione"),
            new("Letture", sample.Operations.ToString("N0"),
                final ? $"{operationsPerSecond:N0} al secondo in media" : $"{sample.OperationsPerSecond:N0} al secondo ora"),
            new("Tempo medio di risposta", $"{sample.AverageLatencyMs:N1} ms", "per ogni cartella o file letto: se cresce, il disco o il NAS sono sotto sforzo"),
            new("Traffico di rete del PC", ResourceSample.FormatBytes(sample.NetworkBytes),
                final ? "ricevuti e inviati durante la scansione (anche da altri programmi)" : $"{ResourceSample.FormatBytes(sample.NetworkBytesPerSecond)}/s ora (anche da altri programmi)"),
            new("Dati letti da Gestione File", sample.AppReadBytes is { } read ? ResourceSample.FormatBytes(read) : "non disponibile",
                "file in rete e locali, compreso l'indice"),
            new("Processore", $"{(final ? sample.AverageCpuPercent : sample.CpuPercent):N1} %",
                final ? "media della scansione, sul totale del PC" : "ora, sul totale del PC (priorità bassa)"),
            new("Memoria", ResourceSample.FormatBytes(sample.WorkingSetBytes), $"massimo {ResourceSample.FormatBytes(sample.PeakWorkingSetBytes)}"),
        };
        ScanResources.Clear();
        foreach (var item in items)
            ScanResources.Add(item);
        return sample;
    }
}
