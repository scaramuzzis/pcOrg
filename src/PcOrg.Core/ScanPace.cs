using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PcOrg.Core;

/// <summary>Velocità con cui Gestione File legge le cartelle (e quindi il carico sul disco o sul server).</summary>
public enum ScanSpeed
{
    /// <summary>Nessun limite: il più veloce, ma impegna di più il disco o il server.</summary>
    Massima,
    /// <summary>Al massimo 150 letture al secondo: carico moderato.</summary>
    Normale,
    /// <summary>Al massimo 25 letture al secondo: carico minimo, adatto a NAS piccoli o a PC usati da altri.</summary>
    Leggera,
}

/// <summary>
/// Limita il ritmo delle letture (elenco di una cartella, lettura di un file) per non sovraccaricare
/// il disco o il computer/NAS che condivide una cartella di rete. Le cartelle locali e quelle di rete
/// hanno ciascuna la propria impostazione. Sicuro da usare da più thread insieme.
/// </summary>
public static class ScanPace
{
    public static ScanSpeed Local { get; set; } = ScanSpeed.Massima;
    public static ScanSpeed Network { get; set; } = ScanSpeed.Normale;

    private static readonly Limiter LocalLimiter = new();
    private static readonly Limiter NetworkLimiter = new();

    /// <summary>Letture al secondo consentite (0 = senza limite).</summary>
    public static int OperationsPerSecond(ScanSpeed speed) => speed switch
    {
        ScanSpeed.Normale => 150,
        ScanSpeed.Leggera => 25,
        _ => 0,
    };

    /// <summary>Attende, se serve, prima di una lettura da una cartella locale o di rete.</summary>
    public static void Wait(bool network, CancellationToken cancellationToken = default)
    {
        var rate = OperationsPerSecond(network ? Network : Local);
        if (rate > 0)
            (network ? NetworkLimiter : LocalLimiter).Wait(rate, cancellationToken);
    }

    /// <summary>Come <see cref="Wait(bool, CancellationToken)"/>, riconoscendo dal percorso se è in rete.</summary>
    public static void WaitFor(string path, CancellationToken cancellationToken = default) =>
        Wait(NetworkPaths.IsNetworkPath(path), cancellationToken);

    /// <summary>Quanti file leggere insieme: in modalità leggera uno alla volta.</summary>
    public static int Parallelism(bool network, int normal) =>
        (network ? Network : Local) == ScanSpeed.Leggera ? 1 : normal;

    /// <summary>
    /// Esegue <paramref name="work"/> con priorità bassa di processore e disco (modalità "in background"
    /// di Windows): il PC resta reattivo per le altre applicazioni mentre Gestione File scansiona.
    /// </summary>
    public static T InBackground<T>(Func<T> work)
    {
        var lowered = OperatingSystem.IsWindows() && SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
        try
        {
            return work();
        }
        finally
        {
            if (lowered)
                SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
        }
    }

    /// <summary>Distanzia le letture in modo da non superare il ritmo indicato.</summary>
    private sealed class Limiter
    {
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _nextFreeMs;

        public void Wait(int perSecond, CancellationToken cancellationToken)
        {
            double delay;
            lock (_gate)
            {
                var now = _clock.Elapsed.TotalMilliseconds;
                var slot = Math.Max(now, _nextFreeMs);
                _nextFreeMs = slot + 1000.0 / perSecond;
                delay = slot - now;
            }
            if (delay >= 1)
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(delay));
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();
}
