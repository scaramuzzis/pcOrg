using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcOrg.App.Infrastructure;

/// <summary>Esito del caricamento di un'anteprima: immagine ridotta e dimensioni originali, oppure un messaggio.</summary>
public sealed record ImagePreviewResult(ImageSource? Image, int PixelWidth, int PixelHeight, string? Message);

/// <summary>Carica in background una versione ridotta di un'immagine, senza bloccare il file su disco.</summary>
public static class ImagePreview
{
    private const int MaxPreviewPixels = 900;
    private const long MaxFileBytes = 300L * 1024 * 1024;

    /// <summary>Formati che Windows sa decodificare (WebP e HEIC richiedono le estensioni dal Microsoft Store).</summary>
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico", ".wdp", ".jxr",
        ".webp", ".heic", ".heif", ".avif",
    };

    public static bool CanPreview(string path) => Supported.Contains(Path.GetExtension(path));

    public static Task<ImagePreviewResult> LoadAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => Load(path), cancellationToken);

    private static ImagePreviewResult Load(string path)
    {
        if (!CanPreview(path))
            return new(null, 0, 0, "Anteprima non disponibile per questo formato.");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return new(null, 0, 0, "Il file non esiste più.");
            if (info.Length > MaxFileBytes)
                return new(null, 0, 0, "Immagine troppo grande per l'anteprima.");

            // Dimensioni originali senza decodificare tutta l'immagine.
            int width, height;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.None);
                width = frame.PixelWidth;
                height = frame.PixelHeight;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // legge tutto subito e rilascia il file
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width >= height && width > MaxPreviewPixels)
                bitmap.DecodePixelWidth = MaxPreviewPixels;
            else if (height > width && height > MaxPreviewPixels)
                bitmap.DecodePixelHeight = MaxPreviewPixels;
            bitmap.EndInit();
            bitmap.Freeze(); // utilizzabile dal thread dell'interfaccia
            return new(bitmap, width, height, null);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException
                                      or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".webp" or ".heic" or ".heif" or ".avif"
                ? new(null, 0, 0, $"Per vedere i file {ext} installa l'estensione gratuita di Microsoft dallo Store.")
                : new(null, 0, 0, "Impossibile leggere l'immagine (file danneggiato o formato non supportato).");
        }
    }
}
