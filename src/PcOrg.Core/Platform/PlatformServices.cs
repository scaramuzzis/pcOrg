using PcOrg.Core.Drivers;
using PcOrg.Core.Installed;
using PcOrg.Core.Metadata;

namespace PcOrg.Core.Platform;

/// <summary>
/// Funzioni che dipendono dal sistema operativo (registro, periferiche, Cestino, firme, pacchetti MSI…).
/// PcOrg.Core usa solo questa interfaccia: l'implementazione per Windows è nel progetto PcOrg.Windows
/// e viene registrata all'avvio dell'app. Senza registrazione (test, altri sistemi) vale
/// <see cref="PortablePlatform"/>, che restituisce elenchi vuoti.
/// </summary>
public interface IPlatformServices
{
    /// <summary>Programmi installati ("App installate" di Windows).</summary>
    IReadOnlyList<InstalledProgram> ReadInstalledPrograms();

    /// <summary>Periferiche presenti (Gestione dispositivi).</summary>
    IReadOnlyList<Device> ReadDevices();

    /// <summary>Sposta un file nel Cestino (mai eliminazione definitiva). Falso se non riuscito.</summary>
    bool SendToRecycleBin(string path);

    /// <summary>Verifica Authenticode: 0 se valida, altrimenti il codice d'errore; null se non disponibile.</summary>
    int? VerifyAuthenticode(string path);

    /// <summary>Editore di un file firmato non PE (es. MSI); null se non firmato o non disponibile.</summary>
    string? SignerFromFile(string path);

    /// <summary>Proprietà di un pacchetto Windows Installer; null se non leggibile o non disponibile.</summary>
    MsiReader.MsiInfo? ReadMsi(string path);

    /// <summary>Percorso di rete di un'unità mappata ("Z:" → "\\NAS\Dati"); null se non è un'unità di rete.</summary>
    string? MappedDriveToUnc(string drive);

    /// <summary>Numero di serie del volume (identifica una chiavetta anche se cambia lettera).</summary>
    uint? VolumeSerial(string root);

    /// <summary>Abbassa la priorità di processore e disco del thread corrente; Dispose la ripristina.</summary>
    IDisposable? EnterBackgroundMode();

    /// <summary>
    /// Scansione veloce: fotografia della Master File Table del volume NTFS che contiene il percorso.
    /// Null se non disponibile (non amministratore, volume non NTFS o di rete): si usa la scansione normale.
    /// </summary>
    Indexing.Ntfs.MftSnapshot? ReadVolumeSnapshot(string path, CancellationToken cancellationToken);

    /// <summary>Vero se l'app ha i privilegi di amministratore.</summary>
    bool IsElevated { get; }

    /// <summary>Byte letti e scritti dal processo dall'avvio (file locali e di rete); null se non disponibile.</summary>
    (long Read, long Written)? ProcessIoCounters();

    /// <summary>
    /// Miniatura del file come in Esplora risorse (fotogramma di un video, copertina di un brano, prima pagina
    /// di un documento); null se il sistema non ne ha una (si esclude l'icona generica del tipo di file).
    /// </summary>
    ThumbnailImage? GetThumbnail(string path, int size);
}

/// <summary>Immagine in pixel BGRA a 32 bit, righe dall'alto in basso.</summary>
public sealed record ThumbnailImage(int Width, int Height, byte[] Bgra);

/// <summary>Implementazione neutra: nessuna funzione di sistema (elenchi vuoti, nessuna verifica).</summary>
public sealed class PortablePlatform : IPlatformServices
{
    public static PortablePlatform Instance { get; } = new();

    public IReadOnlyList<InstalledProgram> ReadInstalledPrograms() => [];
    public IReadOnlyList<Device> ReadDevices() => [];
    public bool SendToRecycleBin(string path) => throw new PlatformNotSupportedException("Il Cestino è disponibile solo su Windows.");
    public int? VerifyAuthenticode(string path) => null;
    public string? SignerFromFile(string path) => null;
    public MsiReader.MsiInfo? ReadMsi(string path) => null;
    public string? MappedDriveToUnc(string drive) => null;
    public uint? VolumeSerial(string root) => null;
    public IDisposable? EnterBackgroundMode() => null;
    public Indexing.Ntfs.MftSnapshot? ReadVolumeSnapshot(string path, CancellationToken cancellationToken) => null;
    public bool IsElevated => false;
    public (long Read, long Written)? ProcessIoCounters() => null;
    public ThumbnailImage? GetThumbnail(string path, int size) => null;
}

/// <summary>Piattaforma in uso: l'app registra quella di Windows all'avvio.</summary>
public static class PlatformServices
{
    public static IPlatformServices Current { get; set; } = PortablePlatform.Instance;
}
