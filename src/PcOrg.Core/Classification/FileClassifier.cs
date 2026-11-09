using PcOrg.Core.Metadata;

namespace PcOrg.Core.Classification;

/// <summary>
/// Assegna una categoria a un file partendo da estensione, nome e cartella.
/// È una prima approssimazione: nelle fasi successive i metadati (versione, firma, MSI)
/// raffineranno la classificazione.
/// </summary>
public static class FileClassifier
{
    private static readonly Dictionary<string, FileCategory> ByExtension = Build(
        (FileCategory.Installer, [".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".msp", ".msu"]),
        (FileCategory.Driver, [".inf", ".sys", ".cat"]),
        (FileCategory.Programma, [".exe", ".com"]),
        (FileCategory.Archivio, [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab", ".zst"]),
        (FileCategory.ImmagineDisco, Subcategories.For(FileCategory.ImmagineDisco).SelectMany(s => s.Extensions).ToArray()),
        (FileCategory.Pdf, [".pdf"]),
        (FileCategory.Word, [".doc", ".docx", ".docm", ".dot", ".dotx", ".odt", ".rtf"]),
        (FileCategory.Testo, [".txt", ".md", ".log", ".nfo"]),
        (FileCategory.FoglioDiCalcolo, [".xls", ".xlsx", ".xlsm", ".ods", ".csv"]),
        (FileCategory.Presentazione, [".ppt", ".pptx", ".odp"]),
        (FileCategory.Documento, [".epub", ".chm", ".xps", ".oxps", ".djvu", ".one", ".pages"]),
        (FileCategory.Immagine, [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".svg", ".ico", ".raw"]),
        (FileCategory.Audio, [".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma", ".opus"]),
        (FileCategory.Video, [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg"]),
        (FileCategory.Script, [".bat", ".cmd", ".ps1", ".vbs", ".reg", ".py", ".sh", ".ahk"]));

    private static readonly string[] InstallerHints = ["setup", "install", "installer", "instalar", "redist", "update"];
    private static readonly string[] DriverHints = ["driver", "drivers", "drv"];

    public static FileCategory Classify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var category = ByExtension.GetValueOrDefault(ext, FileCategory.Altro);

        // Eseguibili e archivi possono essere driver o installer: lo si deduce da nome e cartella.
        if (category is FileCategory.Programma or FileCategory.Archivio)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            if (ContainsWord(stem, DriverHints) || PathContainsFolder(path, DriverHints))
                return FileCategory.Driver;
            if (category == FileCategory.Programma && ContainsWord(stem, InstallerHints))
                return FileCategory.Installer;
        }

        return category;
    }

    /// <summary>
    /// Affina la categoria con i metadati letti dal file: un eseguibile che è un installer riconosciuto,
    /// o che si descrive come "setup", diventa Installer; uno che si descrive come driver diventa Driver.
    /// </summary>
    public static FileCategory Refine(FileCategory category, FileMeta meta)
    {
        if (category is not (FileCategory.Programma or FileCategory.Installer))
            return category;

        var text = $"{meta.FileDescription} {meta.ProductName}";
        if (ContainsWord(text, DriverHints))
            return FileCategory.Driver;
        if (meta.InstallerType is not null || ContainsWord(text, InstallerHints) || ContainsWord(text, ["installazione"]))
            return FileCategory.Installer;
        return category;
    }

    /// <summary>Vero se una delle parole compare nel testo come parola o inizio di parola (es. "nvidia_driver_x64").</summary>
    private static bool ContainsWord(string text, string[] words)
    {
        foreach (var token in Tokenize(text))
            foreach (var word in words)
                if (token.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    private static bool PathContainsFolder(string path, string[] folderNames)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
            return false;
        foreach (var part in dir.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            if (ContainsWord(part, folderNames))
                return true;
        return false;
    }

    /// <summary>Divide un nome in parole: separatori, cifre e passaggi minuscolo→maiuscolo (CamelCase).</summary>
    internal static IEnumerable<string> Tokenize(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isLetter = i < text.Length && char.IsLetter(text[i]);
            var camelBreak = isLetter && start >= 0 && char.IsUpper(text[i]) && char.IsLower(text[i - 1]);
            if (start >= 0 && (!isLetter || camelBreak))
            {
                yield return text[start..i];
                start = -1;
            }
            if (isLetter && start < 0)
                start = i;
        }
    }

    private static Dictionary<string, FileCategory> Build(params (FileCategory Category, string[] Extensions)[] groups)
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, extensions) in groups)
            foreach (var ext in extensions)
                map[ext] = category;
        return map;
    }
}
