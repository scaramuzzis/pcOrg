using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace PcOrg.Core.Content;

/// <summary>
/// Estrae il testo cercabile da un file: il testo dei documenti (PDF, Word, OpenDocument, PowerPoint,
/// Excel, testo semplice) oppure l'elenco dei file contenuti in un archivio ZIP o in un'immagine ISO.
/// </summary>
/// <remarks>
/// Si estrae al massimo <see cref="MaxChars"/> caratteri per file: bastano per ritrovare un documento da
/// una parola e tengono piccolo l'indice. I file corrotti o protetti da password restituiscono null.
/// I formati "Office moderni" (docx, pptx, xlsx, odt) sono archivi ZIP con dentro file XML: si legge
/// l'XML del contenuto e si tolgono i marcatori.
/// </remarks>
public static partial class ContentExtractor
{
    public const int MaxChars = 300_000;
    private const int MaxPdfPages = 300;
    private const int MaxArchiveEntries = 5000;

    /// <summary>Estensioni di cui si indicizza il contenuto.</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".docm", ".dotx", ".odt", ".pptx", ".odp", ".xlsx", ".ods",
        ".txt", ".md", ".log", ".nfo", ".csv", ".zip", ".iso",
    };

    /// <summary>Estensioni lette anche se molto grandi (se ne legge solo l'indice, non i dati).</summary>
    public static readonly IReadOnlySet<string> ContainerExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip", ".iso" };

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex XmlTags();

    [GeneratedRegex(@"[ \t ]{2,}")]
    private static partial Regex Spaces();

    public static string? Extract(string path)
    {
        try
        {
            if (CloudFiles.IsOnlineOnly(path))
                return null; // non si scaricano i file solo online
            var text = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".pdf" => Pdf(path),
                ".docx" or ".docm" or ".dotx" => ZipXml(path, e => e.FullName == "word/document.xml", "</w:p>"),
                ".odt" or ".ods" or ".odp" => ZipXml(path, e => e.FullName == "content.xml", "</text:p>"),
                ".pptx" => ZipXml(path, e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal), "</a:p>"),
                ".xlsx" => ZipXml(path, e => e.FullName == "xl/sharedStrings.xml", "</si>"),
                ".zip" => Listing(ZipNames(path)),
                ".iso" => Listing(IsoReader.ListFiles(path)),
                _ => PlainText(path),
            };
            if (string.IsNullOrWhiteSpace(text))
                return null;
            text = Spaces().Replace(text, " ").Trim();
            return text.Length > MaxChars ? text[..MaxChars] : text;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return null; // file corrotto, protetto o in un formato inatteso
        }
    }

    private static string Pdf(string path)
    {
        using var document = PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in document.GetPages().Take(MaxPdfPages))
        {
            sb.AppendLine(string.Join(' ', page.GetWords().Select(w => w.Text)));
            if (sb.Length > MaxChars)
                break;
        }
        return sb.ToString();
    }

    private static string ZipXml(string path, Func<ZipArchiveEntry, bool> select, string paragraphEnd)
    {
        using var zip = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        foreach (var entry in zip.Entries.Where(select).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var xml = reader.ReadToEnd().Replace(paragraphEnd, paragraphEnd + "\n");
            sb.AppendLine(WebUtility.HtmlDecode(XmlTags().Replace(xml, "")));
            if (sb.Length > MaxChars)
                break;
        }
        return sb.ToString();
    }

    private static List<string> ZipNames(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Where(e => e.Name.Length > 0).Take(MaxArchiveEntries).Select(e => e.FullName).ToList();
    }

    /// <summary>Elenco dei file di un archivio, uno per riga: è il "testo" cercabile dell'archivio.</summary>
    private static string Listing(IReadOnlyCollection<string> names) =>
        names.Count == 0 ? "" : $"Contiene {names.Count} file:\n" + string.Join('\n', names);

    private static string PlainText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[(int)Math.Min(stream.Length, MaxChars * 2L)];
        stream.ReadExactly(bytes);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().IndexOf((byte)0) >= 0)
            return ""; // contiene byte nulli: non è testo
        return Encoding.UTF8.GetString(bytes);
    }
}
