using PcOrg.Core.Classification;
using PcOrg.Core.Metadata;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Tests;

/// <summary>Lettura di versioni, firme e architettura; confronto versioni; raggruppamento prodotti.</summary>
public class MetadataTests
{
    [Fact]
    public void Legge_versione_e_architettura_di_un_assembly_dotnet()
    {
        var path = typeof(PcOrg.Core.PathUtil).Assembly.Location;
        var pe = PeReader.Read(path);

        Assert.NotNull(pe);
        Assert.Equal(Architecture.AnyCpu, pe.Architecture);
        Assert.Equal("PcOrg.Core", pe.Get("ProductName"));
        Assert.Equal("PcOrg.Core.dll", pe.Get("OriginalFilename"));
        Assert.Equal("1.0.0.0", pe.FixedFileVersion);
        Assert.Null(pe.Certificate);
    }

    [Fact]
    public void Legge_l_editore_dalla_firma_di_un_file_microsoft()
    {
        // System.Private.CoreLib.dll è firmato da Microsoft su tutti i sistemi operativi.
        var meta = MetadataExtractor.Extract(typeof(object).Assembly.Location, verifySignature: true);

        Assert.NotNull(meta);
        Assert.Equal("Microsoft Corporation", meta.CompanyName);
        Assert.Equal("Microsoft Corporation", meta.Signer);
        Assert.Contains(meta.SignatureStatus, new[] { SignatureStatus.Valida, SignatureStatus.NonVerificata });
        Assert.NotNull(meta.DisplayVersion);
    }

    [Fact]
    public void Eseguibile_nativo_firmato_verificato_da_windows()
    {
        if (!OperatingSystem.IsWindows())
            return; // la verifica WinVerifyTrust esiste solo su Windows
        var host = Environment.ProcessPath!; // testhost.exe / dotnet.exe, firmati da Microsoft
        var meta = MetadataExtractor.Extract(host);

        Assert.NotNull(meta);
        Assert.Equal(Architecture.X64, meta.Architecture);
        Assert.Equal(SignatureStatus.Valida, meta.SignatureStatus);
        Assert.Equal("Microsoft Corporation", meta.Signer);
    }

    [Fact]
    public void File_non_eseguibili_o_corrotti_restituiscono_null()
    {
        using var dir = new TempDir();
        Assert.Null(MetadataExtractor.Extract(dir.File("finto.exe", "non sono un eseguibile")));
        Assert.Null(MetadataExtractor.Extract(dir.File("troncato.exe", "MZ")));
        Assert.Null(MetadataExtractor.Extract(dir.File("finto.msi", "testo")));
    }

    [Theory]
    [InlineData("566.36", "560.94", 1)]
    [InlineData("3.0.21", "3.0.21.0", 0)]
    [InlineData("3.0.9", "3.0.21", -1)]
    [InlineData("10,0,1426,52326 @Commit", "10.0.1426.52326", 0)]
    [InlineData(null, "1.0", -1)]
    [InlineData("24.08 beta", "24.07", 1)]
    public void Confronto_versioni(string? a, string? b, int expected)
    {
        Assert.Equal(expected, Math.Sign(VersionComparer.Instance.Compare(a, b)));
    }

    [Theory]
    [InlineData("NVIDIA Graphics Driver 566.36", "nvidia graphics driver")]
    [InlineData("Mozilla Firefox (x64 it)", "mozilla firefox")]
    [InlineData("Microsoft® .NET", "microsoft net")]
    [InlineData("vlc-3.0.21-win64", "vlc")]
    [InlineData("ChromeSetup", "chromesetup")]
    [InlineData("7-Zip", "zip")]
    [InlineData("12345", null)]
    public void Normalizzazione_nome_prodotto(string input, string? expected)
    {
        Assert.Equal(expected, ProductKeys.Normalize(input));
    }

    [Fact]
    public void Stesso_prodotto_con_versioni_e_nomi_di_azienda_diversi_ha_la_stessa_chiave()
    {
        var a = new FileMeta { ProductName = "VLC media player", CompanyName = "VideoLAN" };
        var b = new FileMeta { ProductName = "VLC media player 3.0.21", CompanyName = "VideoLAN Team" };
        Assert.Equal(ProductKeys.For(a, "x.exe"), ProductKeys.For(b, "y.exe"));
        Assert.NotEqual(ProductKeys.For(a, "x.exe"), ProductKeys.For(a with { CompanyName = "Altro" }, "x.exe"));
    }

    [Theory]
    [InlineData(FileCategory.Programma, "NVIDIA Display Driver", null, FileCategory.Driver)]
    [InlineData(FileCategory.Programma, "VLC media player", "NSIS", FileCategory.Installer)]
    [InlineData(FileCategory.Programma, "Programma di installazione", null, FileCategory.Installer)]
    [InlineData(FileCategory.Programma, "PuTTY SSH client", null, FileCategory.Programma)]
    [InlineData(FileCategory.Archivio, "qualcosa driver", null, FileCategory.Archivio)]
    public void Affinamento_categoria_con_i_metadati(FileCategory start, string description, string? installer, FileCategory expected)
    {
        var meta = new FileMeta { FileDescription = description, InstallerType = installer };
        Assert.Equal(expected, FileClassifier.Refine(start, meta));
    }

    private static FileRecord File(long id, string version, long size = 10, string key = "vlc|videolan") =>
        new(id, 1, $"C:\\d\\vlc-{version}.exe", $"vlc-{version}.exe", ".exe", "C:\\d", size, DateTime.UtcNow,
            FileCategory.Installer, new FileMeta { ProductName = "VLC media player", CompanyName = "VideoLAN", ProductVersion = version, ProductKey = key });

    [Fact]
    public void Raggruppa_per_prodotto_e_individua_le_versioni_vecchie()
    {
        var groups = ProductCatalog.Build([File(1, "3.0.18", 100), File(2, "3.0.21", 200), File(3, "3.0.21", 200), File(4, "3.0.9", 50),
                                           File(5, "1.0", 5, key: "altro")]);

        var vlc = groups[0];
        Assert.Equal("VLC media player", vlc.Name);
        Assert.Equal("3.0.21", vlc.LatestVersion);
        Assert.Equal(3, vlc.VersionCount);
        Assert.Equal([1L, 4L], vlc.OldVersions.Select(f => f.Id).Order());
        Assert.Equal(150, vlc.OldVersionsSize);
        Assert.Equal(2, groups.Count);
    }
}
