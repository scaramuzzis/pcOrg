namespace PcOrg.Core.Metadata;

/// <summary>Proprietà di un pacchetto Windows Installer (.msi); la lettura è fatta dalla piattaforma (msi.dll).</summary>
public static class MsiReader
{
    /// <summary>Proprietà del pacchetto (tabella Property) e riepilogo "Template" (piattaforma;lingua).</summary>
    public sealed record MsiInfo(IReadOnlyDictionary<string, string> Properties, string? Template);

    public static MsiInfo? Read(string path) => Platform.PlatformServices.Current.ReadMsi(path);

    public static Architecture ArchitectureFromTemplate(string? template)
    {
        var platform = template?.Split(';')[0].Trim().ToLowerInvariant();
        return platform switch
        {
            "x64" or "amd64" or "intel64" => Architecture.X64,
            "arm64" => Architecture.Arm64,
            "arm" => Architecture.Arm,
            "intel" or "" => Architecture.X86,
            _ => Architecture.Sconosciuta,
        };
    }
}
