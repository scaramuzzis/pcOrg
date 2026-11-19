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
