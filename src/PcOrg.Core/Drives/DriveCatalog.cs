namespace PcOrg.Core.Drives;

/// <summary>Un disco collegato al PC (interno, USB, di rete o lettore ottico).</summary>
/// <param name="Name">Radice, es. "E:\".</param>
/// <param name="Id">Identità del disco (numero di serie del volume, o etichetta e dimensione): riconosce
/// una chiavetta anche se Windows le assegna un'altra lettera.</param>
public sealed record DriveSnapshot(string Name, string Label, DriveType Type, string Format, long TotalBytes, long FreeBytes, string Id)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double UsedFraction => TotalBytes <= 0 ? 0 : (double)UsedBytes / TotalBytes;

    public string TypeText => Type switch
    {
        DriveType.Fixed => "Disco interno",
        DriveType.Removable => "Rimovibile (USB)",
        DriveType.Network => "Unità di rete",
        DriveType.CDRom => "CD/DVD",
        DriveType.Ram => "Disco RAM",
        _ => "Altro",
    };

    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? $"{TypeText} ({Name.TrimEnd('\\')})" : $"{Label} ({Name.TrimEnd('\\')})";
}

/// <summary>Elenco dei dischi presenti, letto da Windows.</summary>
public static class DriveCatalog
{
    /// <summary>Dischi pronti all'uso (un lettore DVD vuoto o un lettore di schede senza scheda non compaiono).</summary>
    public static IReadOnlyList<DriveSnapshot> Read()
    {
        var list = new List<DriveSnapshot>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;
                list.Add(new DriveSnapshot(drive.Name, drive.VolumeLabel, drive.DriveType, drive.DriveFormat,
                    drive.TotalSize, drive.AvailableFreeSpace, IdOf(drive.Name, drive.VolumeLabel, drive.TotalSize)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Disco che si scollega mentre lo si legge: si ignora.
            }
        }
        return list;
    }

    private static string IdOf(string root, string label, long size) =>
        Platform.PlatformServices.Current.VolumeSerial(root) is { } serial and not 0 ? serial.ToString("X8") : $"{label}|{size}";
}

/// <summary>
/// Si accorge dei dischi collegati o scollegati confrontando l'elenco a intervalli regolari
/// (funziona per chiavette, dischi USB, schede di memoria e unità di rete mappate).
/// </summary>
public sealed class DriveWatcher : IDisposable
{
    private readonly Func<IReadOnlyList<DriveSnapshot>> _read;
    private readonly Timer _timer;
    private Dictionary<string, DriveSnapshot> _known;
    private int _busy;

    /// <summary>Disco appena collegato.</summary>
    public event Action<DriveSnapshot>? Arrived;

    /// <summary>Disco scollegato.</summary>
    public event Action<DriveSnapshot>? Removed;

    public DriveWatcher(TimeSpan interval, Func<IReadOnlyList<DriveSnapshot>>? read = null)
    {
        _read = read ?? DriveCatalog.Read;
        _known = Snapshot();
        _timer = new Timer(_ => Poll(), null, interval, interval);
    }

    private Dictionary<string, DriveSnapshot> Snapshot() =>
        _read().GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Confronta i dischi presenti con quelli dell'ultima volta (pubblico per i test).</summary>
    public void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            var now = Snapshot();
            foreach (var (name, drive) in now)
                if (!_known.TryGetValue(name, out var old) || old.Id != drive.Id)
                    Arrived?.Invoke(drive);
            foreach (var (name, drive) in _known)
                if (!now.ContainsKey(name))
                    Removed?.Invoke(drive);
            _known = now;
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn("Controllo dei dischi collegati non riuscito.", ex);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
