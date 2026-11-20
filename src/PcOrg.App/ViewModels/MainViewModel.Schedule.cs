using System.Windows.Threading;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Storage;

namespace PcOrg.App.ViewModels;

/// <summary>
/// Quando aggiornare le cartelle. Le cartelle locali costano poco e si aggiornano a ogni avvio; quelle di
/// rete (anche molti GB su un NAS) solo quando è passato l'intervallo scelto nelle impostazioni
/// (4, 8, 12, 24 o 36 ore, oppure solo con F5), sia all'avvio sia mentre Gestione File resta aperto.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Intervalli proposti nelle impostazioni, in ore (0 = solo quando si preme F5).</summary>
    public static readonly int[] NetworkUpdateChoices = [4, 8, 12, 24, 36, 0];

    /// <summary>Ultimo tentativo per cartella di rete (anche se non raggiungibile): evita di riprovare di continuo.</summary>
    private readonly Dictionary<long, DateTime> _networkAttempts = [];

    private DispatcherTimer? _networkTimer;

    /// <summary>Vero se la cartella di rete va aggiornata ora secondo l'intervallo impostato.</summary>
    private bool IsNetworkRootDue(RootFolder root, DateTime nowUtc)
    {
        var hours = _settings.NetworkUpdateHours;
        if (hours <= 0)
            return false; // solo con F5
        var last = root.LastScanUtc ?? DateTime.MinValue;
        if (_networkAttempts.TryGetValue(root.Id, out var attempt) && attempt > last)
            last = attempt;
        return nowUtc - last >= TimeSpan.FromHours(hours);
    }

    /// <summary>Cartelle da aggiornare all'avvio: tutte le locali, le cartelle di rete solo se scadute.</summary>
    private List<RootFolder> RootsForAutomaticUpdate(bool includeLocal)
    {
        var now = DateTime.UtcNow;
        var list = Roots.Select(r => r.Root)
            .Where(r => r.IsNetwork ? IsNetworkRootDue(r, now) : includeLocal)
            .ToList();
        foreach (var r in list.Where(r => r.IsNetwork))
            _networkAttempts[r.Id] = now;
        var skipped = Roots.Count(r => r.Root.IsNetwork) - list.Count(r => r.IsNetwork);
        if (skipped > 0)
            Log.Info($"Aggiornamento automatico: {skipped} cartelle di rete non ancora da aggiornare " +
                     (_settings.NetworkUpdateHours > 0 ? $"(intervallo {_settings.NetworkUpdateHours} ore)." : "(solo con F5)."));
        return list;
    }

    /// <summary>Controlla ogni 10 minuti se qualche cartella di rete è da aggiornare (mentre l'app è aperta).</summary>
    private void StartNetworkSchedule()
    {
        if (_networkTimer is not null)
            return;
        _networkTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMinutes(10) };
        _networkTimer.Tick += (_, _) =>
        {
            if (IsWorking || _settings.NetworkUpdateHours <= 0)
                return;
            var due = RootsForAutomaticUpdate(includeLocal: false);
            if (due.Count > 0)
            {
                Log.Info($"Aggiornamento programmato delle cartelle di rete: {string.Join(", ", due.Select(r => r.Path))}.");
                Fire(ScanRootsAsync(due), "aggiornamento programmato delle cartelle di rete");
            }
        };
        _networkTimer.Start();
    }

    /// <summary>Testo per l'impostazione (es. "Ogni 24 ore", "Solo quando premo F5").</summary>
    public static string DescribeNetworkInterval(int hours) =>
        hours <= 0 ? "Solo quando premo F5 (Aggiorna)" : hours == 24 ? "Una volta al giorno (ogni 24 ore)" : $"Ogni {hours} ore";
}
