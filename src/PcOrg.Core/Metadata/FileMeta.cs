namespace PcOrg.Core.Metadata;

/// <summary>Architettura del processore per cui è compilato un eseguibile o un pacchetto.</summary>
/// <remarks>I valori numerici sono salvati nel database: aggiungere solo in coda.</remarks>
public enum Architecture
{
    Sconosciuta = 0,
    X86 = 1,
    X64 = 2,
    Arm64 = 3,
    Arm = 4,
    /// <summary>Programma .NET che gira su qualsiasi architettura.</summary>
    AnyCpu = 5,
}

/// <summary>Esito del controllo della firma digitale.</summary>
/// <remarks>I valori numerici sono salvati nel database: aggiungere solo in coda.</remarks>
public enum SignatureStatus
{
    /// <summary>Il file non contiene una firma digitale.</summary>
    NonFirmato = 0,
    /// <summary>Firma presente e verificata da Windows.</summary>
    Valida = 1,
    /// <summary>Firma presente ma non valida (file modificato, certificato non attendibile...).</summary>
    NonValida = 2,
    /// <summary>Firma presente ma non verificabile su questo sistema (es. fuori da Windows).</summary>
    NonVerificata = 3,
}

/// <summary>Informazioni lette dal contenuto di un eseguibile (PE) o di un pacchetto MSI.</summary>
public sealed record FileMeta
{
    public string? ProductName { get; init; }
    public string? CompanyName { get; init; }
    public string? FileDescription { get; init; }
    public string? FileVersion { get; init; }
    public string? ProductVersion { get; init; }
    public string? OriginalFilename { get; init; }
    public Architecture Architecture { get; init; }
    public string? Signer { get; init; }
    public SignatureStatus SignatureStatus { get; init; }
    /// <summary>Tecnologia dell'installer, se riconosciuta (Inno Setup, NSIS, Windows Installer...).</summary>
    public string? InstallerType { get; init; }
    /// <summary>Chiave usata per raggruppare le versioni dello stesso prodotto.</summary>
    public string? ProductKey { get; init; }

    /// <summary>
    /// Solo per i file .inf: identificativi hardware delle periferiche supportate. Non sono salvati in
    /// file_meta ma nella tabella driver_hwids, per poterli confrontare velocemente con le periferiche.
    /// </summary>
    public IReadOnlyList<Drivers.DriverHardwareId>? HardwareIds { get; init; }

    /// <summary>Versione da mostrare: quella del prodotto se presente, altrimenti quella del file.</summary>
    public string? DisplayVersion => VersionComparer.Clean(ProductVersion) ?? VersionComparer.Clean(FileVersion);

    /// <summary>Versione prodotto e, se diversa, versione del file.</summary>
    public string? VersionDetails
    {
        get
        {
            var product = ProductVersion?.Trim();
            var file = FileVersion?.Trim();
            if (string.IsNullOrEmpty(product))
                return file;
            return string.IsNullOrEmpty(file) || VersionComparer.AreEqual(product, file) ? product : $"{product}  (file {file})";
        }
    }

    public string ArchitectureText => Architecture switch
    {
        Architecture.X86 => "32 bit (x86)",
        Architecture.X64 => "64 bit (x64)",
        Architecture.Arm64 => "ARM64",
        Architecture.Arm => "ARM",
        Architecture.AnyCpu => "Qualsiasi (.NET)",
        _ => "",
    };

    public string SignatureText => SignatureStatus switch
    {
        SignatureStatus.Valida => $"✔ {Signer}",
        SignatureStatus.NonValida => $"✖ Non valida ({Signer})",
        SignatureStatus.NonVerificata => $"? {Signer}",
        _ => "Non firmato",
    };
}
