using System.Text;
using System.Text.RegularExpressions;

namespace PcOrg.Core.Metadata;

/// <summary>Legge versione, produttore, architettura, firma e tipo di installer da exe, msi e sys.</summary>
public static partial class MetadataExtractor
{
    /// <summary>Estensioni di cui si leggono i metadati (programmi, installer, driver, connessioni remote).</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".exe", ".msi", ".sys", ".vnc", ".rdp", ".inf" };

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>Restituisce null se il file non è leggibile o non è un formato riconosciuto.</summary>
    public static FileMeta? Extract(string path, bool verifySignature = true)
    {
        var ext = Path.GetExtension(path);
        if (RemoteConnectionReader.SupportedExtensions.Contains(ext))
            return RemoteConnectionReader.Read(path);
        if (ext.Equals(".inf", StringComparison.OrdinalIgnoreCase))
            return ExtractInf(path);
        return ext.Equals(".msi", StringComparison.OrdinalIgnoreCase)
            ? ExtractMsi(path, verifySignature)
            : ExtractPe(path, verifySignature);
    }

    private static FileMeta? ExtractPe(string path, bool verifySignature)
    {
        var pe = PeReader.Read(path);
        if (pe is null)
            return null;

        var signer = Signature.SignerFromPkcs7(pe.Certificate);
        var hasSignature = pe.Certificate is not null;
        var meta = new FileMeta
        {
            ProductName = pe.Get("ProductName"),
            CompanyName = pe.Get("CompanyName"),
            FileDescription = pe.Get("FileDescription"),
            FileVersion = pe.Get("FileVersion") ?? pe.FixedFileVersion,
            ProductVersion = pe.Get("ProductVersion") ?? pe.FixedProductVersion,
            OriginalFilename = pe.Get("OriginalFilename"),
            Architecture = pe.Architecture,
            Signer = signer,
            SignatureStatus = verifySignature
                ? Signature.Verify(path, hasSignature)
                : hasSignature ? SignatureStatus.NonVerificata : SignatureStatus.NonFirmato,
            InstallerType = DetectInstaller(path, pe),
        };
        return meta with { ProductKey = ProductKeys.For(meta, path) };
    }

    /// <summary>
    /// Pacchetto driver: il fornitore diventa il produttore, la prima periferica supportata il prodotto
    /// (così le versioni dello stesso driver si raggruppano nella scheda Prodotti) e la versione è DriverVer.
    /// </summary>
    private static FileMeta? ExtractInf(string path)
    {
        if (Drivers.InfReader.Read(path) is not { } inf)
            return null;
        var first = inf.HardwareIds.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Description))?.Description;
        var meta = new FileMeta
        {
            ProductName = first ?? (inf.Class is null ? null : $"Driver {inf.Class}"),
            CompanyName = inf.Provider,
            ProductVersion = inf.Version,
            FileDescription = $"Driver {inf.Class ?? "di periferica"}: {inf.HardwareIds.Count} identificativi hardware supportati" +
                              (inf.Date is { } d ? $", del {d:dd/MM/yyyy}" : ""),
            InstallerType = "Pacchetto driver (INF)",
            HardwareIds = inf.HardwareIds,
        };
        return meta with { ProductKey = ProductKeys.For(meta, path) };
    }

    private static FileMeta? ExtractMsi(string path, bool verifySignature)
    {
        FileMeta meta;
        if (OperatingSystem.IsWindows() && MsiReader.Read(path) is { } msi)
        {
            string? Prop(string name) => msi.Properties.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
            var signer = Signature.SignerFromFile(path);
            meta = new FileMeta
            {
                ProductName = Prop("ProductName"),
                CompanyName = Prop("Manufacturer"),
                ProductVersion = Prop("ProductVersion"),
                Architecture = MsiReader.ArchitectureFromTemplate(msi.Template),
                Signer = signer,
                SignatureStatus = verifySignature
                    ? Signature.Verify(path, signer is not null)
                    : signer is null ? SignatureStatus.NonFirmato : SignatureStatus.NonVerificata,
                InstallerType = "Windows Installer (MSI)",
            };
        }
        else
        {
            // Fuori da Windows (o MSI illeggibile) si riconosce almeno il formato.
            if (!HasOleHeader(path))
                return null;
            meta = new FileMeta { InstallerType = "Windows Installer (MSI)" };
        }
        return meta with { ProductKey = ProductKeys.For(meta, path) };
    }

    private static bool HasOleHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[8];
            return stream.Read(header) == 8 && header.SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Riconosce i generatori di installer più diffusi da sezioni, stringhe di versione e dati accodati.</summary>
    internal static string? DetectInstaller(string path, PeInfo pe)
    {
        if (pe.SectionNames.Contains(".wixburn"))
            return "WiX Burn";

        var text = string.Join(" ", pe.VersionStrings.Values);
        if (text.Contains("Inno Setup", StringComparison.OrdinalIgnoreCase))
            return "Inno Setup";
        if (text.Contains("InstallShield", StringComparison.OrdinalIgnoreCase))
            return "InstallShield";
        if (text.Contains("Nullsoft", StringComparison.OrdinalIgnoreCase))
            return "NSIS";
        if (text.Contains("Advanced Installer", StringComparison.OrdinalIgnoreCase))
            return "Advanced Installer";

        if (pe.OverlayOffset <= 0)
            return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (pe.OverlayOffset >= stream.Length)
                return null;
            const int Window = 512 * 1024;
            if (PeReader.Contains(stream, pe.OverlayOffset, Window, "NullsoftInst"u8))
                return "NSIS";
            if (PeReader.Contains(stream, pe.OverlayOffset, Window, "Inno Setup"u8))
                return "Inno Setup";
            if (PeReader.Contains(stream, pe.OverlayOffset, 64, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]))
                return "Autoestraente 7-Zip";
            if (PeReader.Contains(stream, pe.OverlayOffset, 64, "PK\x03\x04"u8))
                return "Autoestraente ZIP";
            if (PeReader.Contains(stream, pe.OverlayOffset, 64, "Rar!"u8))
                return "Autoestraente RAR";
            if (PeReader.Contains(stream, pe.OverlayOffset, 64, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
                return "Contiene un MSI";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }
}

/// <summary>Costruisce la chiave che raggruppa versioni diverse dello stesso prodotto.</summary>
public static partial class ProductKeys
{
    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup", "installer", "install", "installation", "web", "offline", "full", "x64", "x86", "x32", "amd64",
        "arm64", "win64", "win32", "win", "windows", "64bit", "32bit", "64-bit", "32-bit", "bit", "edition",
        "portable", "release", "final", "beta", "en", "it", "en-us", "it-it", "multilingual",
    };

    private static readonly HashSet<string> CompanySuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "corporation", "corp", "inc", "incorporated", "ltd", "limited", "llc", "gmbh", "ag", "co", "company",
        "s.r.l", "srl", "s.p.a", "spa", "sa", "bv", "oy", "ab", "plc", "the",
    };

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]|[®™©]")]
    private static partial Regex Decorations();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+#]*|\d+(?:\.\d+)*")]
    private static partial Regex NameTokens();

    public static string? For(FileMeta meta, string path)
    {
        var product = Normalize(meta.ProductName ?? meta.FileDescription);
        if (product is null)
            product = Normalize(Path.GetFileNameWithoutExtension(path));
        if (product is null)
            return null;
        var company = CompanyWord(meta.CompanyName);
        return company is null ? product : $"{product}|{company}";
    }

    /// <summary>"NVIDIA Graphics Driver 566.36 (x64)" → "nvidia graphics driver".</summary>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var cleaned = Decorations().Replace(name, " ");
        var words = new List<string>();
        foreach (Match m in NameTokens().Matches(cleaned))
        {
            var token = m.Value;
            if (char.IsDigit(token[0]) || NoiseWords.Contains(token))
                continue;
            // "v2", "win10", "x64" e simili: versioni o piattaforme, non parte del nome.
            if (token.Length <= 6 && token.Any(char.IsDigit) && (token[0] is 'v' or 'V' || token.StartsWith("win", StringComparison.OrdinalIgnoreCase)))
                continue;
            words.Add(token.ToLowerInvariant());
        }
        return words.Count == 0 ? null : string.Join(' ', words);
    }

    private static string? CompanyWord(string? company)
    {
        if (string.IsNullOrWhiteSpace(company))
            return null;
        var words = Decorations().Replace(company, " ")
            .Split([' ', ',', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !CompanySuffixes.Contains(w.TrimEnd('.')))
            .ToList();
        return words.Count == 0 ? null : words[0].ToLowerInvariant();
    }
}
