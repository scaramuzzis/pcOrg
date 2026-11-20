using System.Diagnostics;
using System.Net.NetworkInformation;
using PcOrg.Core.Drives;

namespace PcOrg.Core.Diagnostics;

/// <summary>Risorse usate da una scansione fino a un certo momento.</summary>
/// <param name="CpuPercent">Processore usato da Gestione File nell'ultimo intervallo, in % del totale del PC.</param>
/// <param name="AppReadBytes">Byte letti da Gestione File (file in rete e locali, indice); null se non misurabile.</param>
/// <param name="NetworkBytes">Traffico di rete del PC (ricevuti + inviati) dall'inizio della scansione.</param>
/// <param name="Operations">Letture fatte (elenchi di cartelle e file aperti) dall'inizio della scansione.</param>
public sealed record ResourceSample(
    TimeSpan Elapsed,
    double CpuPercent,
    double AverageCpuPercent,
    long WorkingSetBytes,
    long PeakWorkingSetBytes,
    long? AppReadBytes,
    long NetworkBytes,
    double NetworkBytesPerSecond,
    long Operations,
    double OperationsPerSecond,
    double AverageLatencyMs,
    TimeSpan? Remaining)
{
    /// <summary>Riga compatta per la barra di stato.</summary>
    public string ShortText =>
        $"Rete {FormatBytes(NetworkBytesPerSecond)}/s · {OperationsPerSecond:N0} letture/s · CPU {CpuPercent:N0}% · " +
        $"RAM {FormatBytes(WorkingSetBytes)}" + (Remaining is { } r ? $" · mancano circa {FormatDuration(r)}" : "");

    /// <summary>Riepilogo per il registro attività.</summary>
    public string Summary =>
        $"durata {FormatDuration(Elapsed)}, {Operations:N0} letture ({Operations / Math.Max(1, Elapsed.TotalSeconds):N0}/s, " +
        $"tempo medio {AverageLatencyMs:N1} ms), traffico di rete del PC {FormatBytes(NetworkBytes)}" +
        (AppReadBytes is { } read ? $", dati letti da Gestione File {FormatBytes(read)}" : "") +
        $", CPU media {AverageCpuPercent:N1}%, memoria massima {FormatBytes(PeakWorkingSetBytes)}";

    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} {units[unit]}" : $"{bytes:N1} {units[unit]}";
    }

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:D2} min"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:D2} s"
        : $"{Math.Max(0, t.Seconds)} s";
}

/// <summary>
/// Misura le risorse usate durante una scansione: processore e memoria di Gestione File, dati letti
/// dall'app, traffico di rete del PC e letture fatte sui dischi (vedi <see cref="DiskActivity"/>).
/// Si crea all'inizio della scansione e si interroga a intervalli regolari con <see cref="Sample"/>.
/// </summary>
public sealed class ResourceMonitor
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Func<(long Read, long Written)?> _processIo;
    private readonly Func<long> _networkBytes;
    private readonly Func<(long Operations, TimeSpan Time)> _activity;
    private readonly TimeSpan _cpuStart;
    private readonly long? _readStart;
    private readonly long _networkStart;
    private readonly (long Operations, TimeSpan Time) _activityStart;

    private TimeSpan _lastClock;
    private TimeSpan _lastCpu;
    private long _lastNetwork;
    private long _lastOperations;
    private long _peakWorkingSet;

    /// <summary>Lavoro totale previsto (es. cartelle già note nell'indice) per stimare il tempo rimanente; 0 = sconosciuto.</summary>
    public long ExpectedWork { get; set; }

    /// <summary>Lavoro già fatto (es. cartelle controllate), aggiornato da chi esegue la scansione.</summary>
    public long DoneWork { get; set; }

    /// <param name="processIo">Byte letti e scritti dal processo (null se il sistema non lo fornisce).</param>
    public ResourceMonitor(Func<(long Read, long Written)?>? processIo = null, Func<long>? networkBytes = null,
        Func<(long Operations, TimeSpan Time)>? activity = null)
    {
        _processIo = processIo ?? (() => null);
        _networkBytes = networkBytes ?? TotalNetworkBytes;
        _activity = activity ?? TotalActivity;
        _cpuStart = _lastCpu = CurrentCpu();
        _readStart = _processIo()?.Read;
        _networkStart = _lastNetwork = SafeNetwork();
        _activityStart = _activity();
        _lastOperations = _activityStart.Operations;
    }

    public ResourceSample Sample()
    {
        var now = _clock.Elapsed;
        var interval = Math.Max(0.001, (now - _lastClock).TotalSeconds);
        var cpu = CurrentCpu();
        var cores = Math.Max(1, Environment.ProcessorCount);
        var cpuPercent = Math.Clamp((cpu - _lastCpu).TotalSeconds / interval / cores * 100, 0, 100);
        var averageCpu = Math.Clamp((cpu - _cpuStart).TotalSeconds / Math.Max(0.001, now.TotalSeconds) / cores * 100, 0, 100);

        using var process = Process.GetCurrentProcess();
        var workingSet = process.WorkingSet64;
        _peakWorkingSet = Math.Max(_peakWorkingSet, workingSet);

        var network = SafeNetwork();
        var activity = _activity();
        var operations = Math.Max(0, activity.Operations - _activityStart.Operations);
        var latency = operations == 0 ? 0 : (activity.Time - _activityStart.Time).TotalMilliseconds / operations;
        long? read = _readStart is { } start && _processIo() is { } io ? Math.Max(0, io.Read - start) : null;

        TimeSpan? remaining = null;
        if (ExpectedWork > 0 && DoneWork > 0 && DoneWork < ExpectedWork && now.TotalSeconds >= 3)
            remaining = TimeSpan.FromSeconds(now.TotalSeconds / DoneWork * (ExpectedWork - DoneWork));

        var sample = new ResourceSample(now, cpuPercent, averageCpu, workingSet, _peakWorkingSet, read,
            Math.Max(0, network - _networkStart), Math.Max(0, network - _lastNetwork) / interval,
            operations, Math.Max(0, activity.Operations - _lastOperations) / interval, latency, remaining);

        _lastClock = now;
        _lastCpu = cpu;
        _lastNetwork = network;
        _lastOperations = activity.Operations;
        return sample;
    }

    private static TimeSpan CurrentCpu()
    {
        using var process = Process.GetCurrentProcess();
        return process.TotalProcessorTime;
    }

    private long SafeNetwork()
    {
        try
        {
            return _networkBytes();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>Byte ricevuti e inviati da tutte le schede di rete attive (escluso il loopback).</summary>
    public static long TotalNetworkBytes()
    {
        long total = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var stats = nic.GetIPStatistics();
            total += stats.BytesReceived + stats.BytesSent;
        }
        return total;
    }

    private static (long Operations, TimeSpan Time) TotalActivity()
    {
        long operations = 0;
        var time = TimeSpan.Zero;
        foreach (var d in DiskActivity.All())
        {
            operations += d.Operations;
            time += d.TotalTime;
        }
        return (operations, time);
    }
}
