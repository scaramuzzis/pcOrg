using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PcOrg.Core;
using PcOrg.Core.Classification;
using PcOrg.Core.Content;
using PcOrg.Core.Platform;

namespace PcOrg.App.Infrastructure;

/// <summary>Che cosa mostra il pannello anteprima.</summary>
public enum PreviewKind
{
    None,
    /// <summary>Foto e immagini, decodificate da WPF.</summary>
    Image,
    /// <summary>Video e audio: miniatura di Windows e pulsante Riproduci.</summary>
    Media,
    /// <summary>PDF, Word, Excel, PowerPoint…: miniatura (se Windows la fornisce) ed estratto del testo.</summary>
    Document,
    /// <summary>File di testo: le prime righe.</summary>
    Text,
    /// <summary>Altri formati di cui Windows ha una miniatura.</summary>
    Thumbnail,
}

/// <summary>Risultato di un'anteprima: immagine o miniatura, testo, riga informativa.</summary>
public sealed record FilePreviewResult(PreviewKind Kind, ImageSource? Image, string? Text, string Info);

/// <summary>
/// Anteprima di un file nel pannello a destra: immagini, fotogramma dei video, copertina dei brani, prima
/// pagina e testo dei documenti, prime righe dei file di testo. Tutto in background e senza bloccare il file.
/// </summary>
public static class FilePreview
{
    private const int ThumbnailSize = 600;
    private const int MaxTextChars = 4000;
    private const int MaxDocumentChars = 1500;
    private const long MaxDocumentBytes = 40L * 1024 * 1024;

    /// <summary>File di testo di cui mostrare le prime righe.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".nfo", ".csv", ".tsv", ".json", ".xml", ".ini", ".cfg", ".conf", ".yml", ".yaml",
        ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".reg", ".inf", ".sql", ".py", ".cs", ".js", ".ts", ".html", ".htm",
        ".css", ".sh", ".rdp", ".vnc", ".srt", ".sub", ".url", ".gitignore", ".editorconfig",
    };

    /// <summary>Documenti di cui si mostra un estratto del testo.</summary>
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".docm", ".dotx", ".odt", ".pptx", ".odp", ".xlsx", ".ods",
    };

    /// <summary>Tipo di anteprima previsto dall'estensione (Thumbnail = da provare: dipende da Windows).</summary>
    public static PreviewKind ExpectedKind(string path, FileCategory category)
    {
        var ext = Path.GetExtension(path);
        if (ImagePreview.CanPreview(path) || category == FileCategory.Immagine)
            return PreviewKind.Image;
        if (category is FileCategory.Video or FileCategory.Audio)
            return PreviewKind.Media;
        if (DocumentExtensions.Contains(ext))
            return PreviewKind.Document;
        if (TextExtensions.Contains(ext) || category == FileCategory.Testo)
            return PreviewKind.Text;
        // Programmi, archivi e immagini disco: Windows mostrerebbe solo l'icona, non serve chiederla.
        return category is FileCategory.Programma or FileCategory.Installer or FileCategory.Driver or FileCategory.Archivio
            or FileCategory.ImmagineDisco or FileCategory.Script
            ? PreviewKind.None
            : PreviewKind.Thumbnail;
    }

    public static Task<FilePreviewResult> LoadAsync(string path, FileCategory category, long size, CancellationToken cancellationToken) =>
        Task.Run(() => Load(path, category, size), cancellationToken);

    private static FilePreviewResult Load(string path, FileCategory category, long size)
    {
        var kind = ExpectedKind(path, category);
        var sizeText = FileSizeConverter.Format(size);
        if (kind == PreviewKind.None)
            return new(PreviewKind.None, null, null, "");
        try
        {
            if (!File.Exists(path))
                return new(kind, null, null, "Il file non esiste più.");
            if (CloudFiles.IsOnlineOnly(path))
                return new(kind, null, null, "File disponibile solo online (es. OneDrive): l'anteprima lo farebbe scaricare. Aprilo per scaricarlo.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(kind, null, null, "Il file non è raggiungibile.");
        }

        switch (kind)
        {
            case PreviewKind.Image:
                var image = ImagePreview.Load(path);
                if (image.Image is null && Thumbnail(path) is { } fallback)
                    return new(PreviewKind.Image, fallback, null, $"{sizeText} · miniatura di Windows");
                return new(PreviewKind.Image, image.Image, null,
                    image.Message ?? $"{image.PixelWidth} × {image.PixelHeight} pixel · {sizeText}");

            case PreviewKind.Media:
                var cover = Thumbnail(path);
                var what = category == FileCategory.Video ? "Video" : "Audio";
                return new(PreviewKind.Media, cover, null,
                    $"{what} · {sizeText}" + (cover is null && category == FileCategory.Video ? " · miniatura non disponibile (manca il codec?)" : ""));

            case PreviewKind.Document:
                var page = Thumbnail(path);
                var text = size <= MaxDocumentBytes ? Shorten(ContentExtractor.Extract(path), MaxDocumentChars) : null;
                return new(PreviewKind.Document, page, text,
                    sizeText + (text is null ? " · testo non disponibile" : " · inizio del testo"));

            case PreviewKind.Text:
                var lines = ReadText(path);
                return new(PreviewKind.Text, null, lines, lines is null ? $"{sizeText} · il contenuto non è testo leggibile" : $"{sizeText} · prime righe");

            default:
                var thumbnail = Thumbnail(path);
                return thumbnail is null
                    ? new(PreviewKind.None, null, null, "")
                    : new(PreviewKind.Thumbnail, thumbnail, null, $"{sizeText} · miniatura di Windows");
        }
    }

    /// <summary>Miniatura di Esplora risorse convertita per WPF (null se non disponibile).</summary>
    private static BitmapSource? Thumbnail(string path)
    {
        var thumb = PlatformServices.Current.GetThumbnail(path, ThumbnailSize);
        if (thumb is null)
            return null;
        var bitmap = BitmapSource.Create(thumb.Width, thumb.Height, 96, 96, PixelFormats.Bgra32, null, thumb.Bgra, thumb.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Prime righe di un file di testo (UTF-8 o, se non valido, la codifica di Windows).</summary>
    public static string? ReadText(string path)
    {
        try
        {
            var buffer = new byte[MaxTextChars * 2];
            int read;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                read = stream.Read(buffer, 0, buffer.Length);
            var bytes = buffer.AsSpan(0, read);
            if (bytes.IndexOf((byte)0) >= 0 && !(read >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE))
                return null; // binario (o UTF-16 senza BOM): non è testo leggibile
            string text;
            if (read >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                text = Encoding.Unicode.GetString(bytes[2..]);
            else
            {
                try
                {
                    text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                }
                catch (DecoderFallbackException)
                {
                    text = Encoding.Latin1.GetString(bytes);
                }
            }
            return Shorten(text.TrimStart('﻿'), MaxTextChars);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Shorten(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Replace("\r\n", "\n").Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + " …";
    }
}
