using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using PcOrg.Core.Updates;

namespace PcOrg.Core.Tests;

public class UpdateTests
{
    private const string ReleaseJson = """
        {"tag_name":"v2.0.0","html_url":"https://example.invalid/r","assets":[
          {"name":"GestioneFile-portatile-win-x64.zip","browser_download_url":"https://example.invalid/p.zip","size":10,"digest":"sha256:ABCDEF"},
          {"name":"GestioneFile.exe","browser_download_url":"https://example.invalid/g.exe","size":5},
          {"name":"senza-url"}
        ]}
        """;

    [Fact]
    public void Legge_i_pacchetti_della_release()
    {
        var info = UpdateChecker.Parse(ReleaseJson, "1.2.2");
        Assert.Equal(UpdateStatus.NuovaVersione, info.Status);
        Assert.Equal(2, info.Assets.Count);
        Assert.Equal("abcdef", info.Assets[0].Sha256);
        Assert.Null(info.Assets[1].Sha256);

        Assert.Equal("https://example.invalid/p.zip", AppUpdater.SelectAsset(info, InstallKind.Portatile)?.DownloadUrl);
        Assert.Equal("https://example.invalid/g.exe", AppUpdater.SelectAsset(info, InstallKind.FileUnico)?.DownloadUrl);
        Assert.Null(AppUpdater.SelectAsset(info, InstallKind.Leggero));
    }

    [Fact]
    public void Riconosce_il_tipo_di_installazione()
    {
        using var dir = new TempDir();
        Assert.Equal(InstallKind.FileUnico, AppUpdater.DetectKind(dir.Path, isSingleFile: true));
        Assert.Equal(InstallKind.Leggero, AppUpdater.DetectKind(dir.Path, isSingleFile: false));
        dir.File("coreclr.dll");
        Assert.Equal(InstallKind.Portatile, AppUpdater.DetectKind(dir.Path, isSingleFile: false));
        dir.File(AppUpdater.InstalledMarker);
        Assert.Equal(InstallKind.Installato, AppUpdater.DetectKind(dir.Path, isSingleFile: false));
    }

    [Fact]
    public async Task Scarica_e_verifica_l_impronta()
    {
        using var dir = new TempDir();
        var payload = "contenuto del pacchetto"u8.ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        using var client = new HttpClient(new BytesHandler(payload));

        var ok = new ReleaseAsset("GestioneFile.exe", "https://example.invalid/g.exe", payload.Length, sha);
        var file = await AppUpdater.DownloadAsync(ok, dir.Path, "1.0", client: client);
        Assert.Equal(payload, File.ReadAllBytes(file));

        var wrongHash = ok with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdater.DownloadAsync(wrongHash, dir.Path, "1.0", client: client));
        Assert.False(File.Exists(file)); // il file non valido viene eliminato

        var wrongSize = ok with { Size = payload.Length + 1 };
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdater.DownloadAsync(wrongSize, dir.Path, "1.0", client: client));
    }

    [Fact]
    public void Prepara_lo_zip_e_controlla_che_contenga_l_eseguibile()
    {
        using var dir = new TempDir();
        var source = dir.Dir("sorgente/GestioneFile");
        File.WriteAllText(Path.Combine(source, "GestioneFile.exe"), "exe");
        var zip = Path.Combine(dir.Dir("scaricato"), "p.zip");
        ZipFile.CreateFromDirectory(Path.Combine(dir.Path, "sorgente"), zip);

        var prepared = AppUpdater.Prepare(InstallKind.Portatile, zip, "2.0.0");
        Assert.True(File.Exists(Path.Combine(prepared.Source, "GestioneFile.exe")));

        var empty = Path.Combine(dir.Dir("vuoto"), "v.zip");
        using (ZipFile.Open(empty, ZipArchiveMode.Create)) { }
        Assert.Throws<InvalidDataException>(() => AppUpdater.Prepare(InstallKind.Leggero, empty, "2.0.0"));
    }

    [Theory]
    [InlineData(InstallKind.Portatile, "robocopy")]
    [InlineData(InstallKind.FileUnico, "Copy-Item")]
    [InlineData(InstallKind.Installato, "msiexec.exe")]
    public void Lo_script_attende_sostituisce_e_riavvia(InstallKind kind, string command)
    {
        var update = new PreparedUpdate(kind, @"C:\Temp\Dell'utente\nuovo", "2.0.0");
        var script = AppUpdater.BuildScript(update, @"C:\App", @"C:\App\GestioneFile.exe", 4242, @"C:\Dati\log\pcorg.log");
        Assert.Contains("Wait-Process -Id 4242", script);
        Assert.Contains(command, script);
        Assert.Contains(@"'C:\Temp\Dell''utente\nuovo'", script.Replace("\"", "")); // apice raddoppiato
        Assert.Contains(@"Start-Process -FilePath 'C:\App\GestioneFile.exe'", script);
    }

    private sealed class BytesHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
    }
}

public class WingetCatalogTests
{
    private static readonly WingetCatalog Catalog = WingetCatalog.FromPackages(
    [
        new("Mozilla.Firefox", "Mozilla Firefox", "160.0.2", "Mozilla"),
        new("OpenJS.NodeJS", "Node.js", "24.23.0", "OpenJS Foundation"),
        new("OpenJS.NodeJS.LTS", "Node.js LTS", "22.20.0", "OpenJS Foundation"),
        new("Python.Python.3.13", "Python 3.13", "3.13.9", "Python Software Foundation"),
        new("Python.Python.3.14", "Python 3.14", "3.14.11", "Python Software Foundation"),
        new("Altro.Setup", "Setup", "1.0"),
        new("Rotto.Pacchetto", "Pacchetto", "Unknown"),
    ]);

    [Theory]
    [InlineData("Firefox", "Mozilla Corporation", "159.0.1", "Mozilla.Firefox", true)]
    [InlineData("Mozilla Firefox", "Mozilla Corporation", "160.0.2", "Mozilla.Firefox", false)]
    [InlineData("Node.js", "Node.js Foundation", "24.22.2", null, false)]   // produttore diverso: nessuna corrispondenza
    [InlineData("Node.js", "OpenJS Foundation", "24.22.2", "OpenJS.NodeJS", true)]
    [InlineData("Python 3.14.10 (64-bit)", "Python Software Foundation", "3.14.10", "Python.Python.3.14", true)]
    [InlineData("Python 3.13.2 (64-bit)", "Python Software Foundation", "3.13.2", "Python.Python.3.13", true)]
    [InlineData("Firefox", "Altra Ditta", "1.0", null, false)]
    [InlineData("Pacchetto", null, "1.0", null, false)]                       // versione "Unknown" ignorata
    public void Riconosce_i_prodotti(string name, string? company, string local, string? expectedId, bool newer)
    {
        var match = Catalog.Match(name, company, local);
        Assert.Equal(expectedId, match?.Id);
        if (match is not null)
            Assert.Equal(newer, match.IsNewer);
    }

    [Fact]
    public void Normalizza_i_nomi()
    {
        Assert.Equal("python", WingetCatalog.Normalize("Python 3.14.10 (64-bit)"));
        Assert.Equal("nodejs", WingetCatalog.Normalize("Node.js"));
        Assert.Equal("mozilla", WingetCatalog.CompanyWord("The Mozilla Corporation"));
    }

    [Fact]
    public void Legge_l_indice_nei_due_formati()
    {
        using var dir = new TempDir();
        var v2 = Path.Combine(dir.Path, "v2.db");
        Exec(v2, """
            CREATE TABLE packages (rowid INTEGER PRIMARY KEY, id TEXT, name TEXT, moniker TEXT, latest_version TEXT);
            CREATE TABLE norm_publishers2_0 (norm_publisher TEXT, package INT64);
            INSERT INTO packages VALUES (1, 'Mozilla.Firefox', 'Mozilla Firefox', 'firefox', '160.0.2');
            INSERT INTO norm_publishers2_0 VALUES ('mozilla', 1);
            """);
        Assert.Equal("Mozilla.Firefox", WingetCatalog.Load(v2).Match("Firefox", "Mozilla Corporation", "150")?.Id);

        var v1 = Path.Combine(dir.Path, "v1.db");
        Exec(v1, """
            CREATE TABLE ids (rowid INTEGER PRIMARY KEY, id TEXT);
            CREATE TABLE names (rowid INTEGER PRIMARY KEY, name TEXT);
            CREATE TABLE versions (rowid INTEGER PRIMARY KEY, version TEXT);
            CREATE TABLE manifest (rowid INTEGER PRIMARY KEY, id INT64, name INT64, version INT64);
            INSERT INTO ids VALUES (1, 'Mozilla.Firefox');
            INSERT INTO names VALUES (1, 'Mozilla Firefox');
            INSERT INTO versions VALUES (1, '159.0'), (2, '160.0.2'), (3, '99.0');
            INSERT INTO manifest VALUES (1, 1, 1, 1), (2, 1, 1, 2), (3, 1, 1, 3);
            """);
        Assert.Equal("160.0.2", WingetCatalog.Load(v1).Match("Firefox", "Mozilla", "150")?.LatestVersion);
    }

    /// <summary>Prova reale con l'indice pubblico: gira solo se PCORG_ONLINE_TESTS=1 (build su GitHub).</summary>
    [Fact]
    public async Task Indice_reale_di_winget()
    {
        if (Environment.GetEnvironmentVariable("PCORG_ONLINE_TESTS") != "1")
            return;
        using var dir = new TempDir();
        var file = await WingetCatalog.EnsureIndexAsync(dir.Path, "test");
        var catalog = WingetCatalog.Load(file);
        Assert.True(catalog.Count > 1000, $"Solo {catalog.Count} pacchetti");
        Assert.Equal("Mozilla.Firefox", catalog.Match("Firefox", "Mozilla Corporation", "100.0")?.Id);
        Assert.NotNull(catalog.Match("Node.js", "OpenJS Foundation", "20.0.0"));
        Assert.Equal("7zip.7zip", catalog.Match("7-Zip", "Igor Pavlov", "19.00")?.Id);
    }

    private static void Exec(string file, string sql)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
