using PcOrg.Core.Classification;
using PcOrg.Core.Installed;
using PcOrg.Core.Metadata;
using PcOrg.Core.Moving;
using PcOrg.Core.Rules;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Tests;

/// <summary>Confronto con i programmi installati e regole di archiviazione.</summary>
public class InstalledAndRulesTests
{
    private static FileRecord Installer(string product, string company, string? version, string path = @"C:\Download\setup.exe",
        FileCategory category = FileCategory.Installer)
    {
        var meta = new FileMeta { ProductName = product, CompanyName = company, ProductVersion = version };
        meta = meta with { ProductKey = ProductKeys.For(meta, path) };
        return new FileRecord(1, 1, path, Path.GetFileName(path), Path.GetExtension(path), Path.GetDirectoryName(path)!,
            1000, new DateTime(2024, 3, 15, 10, 0, 0, DateTimeKind.Utc), category, meta);
    }

    private static readonly IReadOnlyList<InstalledProgram> Installed =
    [
        InstalledPrograms.Create("VLC media player", "3.0.20", "VideoLAN"),
        InstalledPrograms.Create("7-Zip 24.08 (x64)", "24.08", "Igor Pavlov"),
        InstalledPrograms.Create("Mozilla Firefox (x64 it)", "133.0", "Mozilla"),
        InstalledPrograms.Create("Notepad++ (64-bit x64)", null, "Notepad++ Team"),
    ];

    [Theory]
    [InlineData("VLC media player", "VideoLAN", "3.0.21", InstallState.InstallataPiuVecchia)]
    [InlineData("VLC media player", "VideoLAN", "3.0.20", InstallState.StessaVersione)]
    [InlineData("7-Zip", "Igor Pavlov", "24.08", InstallState.StessaVersione)]
    [InlineData("Mozilla Firefox", "Mozilla Corporation", "132.0", InstallState.InstallataPiuRecente)]
    [InlineData("Notepad++", "Notepad++ Team", "8.7", InstallState.Installato)]
    [InlineData("GIMP", "The GIMP Team", "2.10", InstallState.NonInstallato)]
    public void Confronta_gli_installer_con_i_programmi_installati(string product, string company, string version, InstallState expected)
    {
        Assert.Equal(expected, InstalledPrograms.Match(Installer(product, company, version), Installed).State);
    }

    [Fact]
    public void I_documenti_non_vengono_confrontati()
    {
        var pdf = Installer("VLC media player", "VideoLAN", "1", @"C:\Doc\vlc.pdf", FileCategory.Pdf);
        Assert.Equal(InstallState.Sconosciuto, InstalledPrograms.Match(pdf, Installed).State);
    }

    [Fact]
    public void La_lettura_del_registro_non_fallisce()
    {
        var programs = InstalledPrograms.Read(); // su Windows legge davvero, altrove restituisce un elenco vuoto
        Assert.NotNull(programs);
        if (OperatingSystem.IsWindows())
            Assert.All(programs, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }

    [Fact]
    public void Le_regole_risolvono_i_segnaposto()
    {
        var rule = new ArchiveRule { Category = FileCategory.Driver, CompanyContains = "nvidia", Destination = @"D:\Archivio\{Categoria}\{Produttore}\{Anno}" };
        var driver = Installer("NVIDIA Graphics Driver", "NVIDIA Corporation", "566.36", @"C:\Download\566.36-driver.exe", FileCategory.Driver);

        Assert.True(rule.Matches(driver));
        Assert.Equal(@"D:\Archivio\Driver\NVIDIA\2024", RuleEngine.ResolveDestination(rule, driver));
        Assert.False(rule.Matches(Installer("VLC", "VideoLAN", "1")));
        Assert.Equal("categoria Driver, produttore contiene \"nvidia\"", rule.ConditionText);
    }

    [Fact]
    public void I_caratteri_non_ammessi_vengono_sostituiti()
    {
        var rule = new ArchiveRule { Destination = @"D:\Archivio\{Prodotto}" };
        var file = Installer("Strano: <prodotto>?", "X", "1");
        Assert.Equal(@"D:\Archivio\Strano_ _prodotto__", RuleEngine.ResolveDestination(rule, file));
    }

    [Fact]
    public void Il_piano_usa_la_prima_regola_e_non_sovrascrive()
    {
        using var dir = new TempDir();
        var a = dir.File("Download/nvidia-driver.exe");
        var b = dir.File("Download/sub/nvidia-driver.exe");
        var c = dir.File("Download/manuale.pdf");
        var archive = System.IO.Path.Combine(dir.Path, "Archivio");
        FileRecord F(string path, FileCategory cat, string? company) =>
            new(0, 1, path, System.IO.Path.GetFileName(path), System.IO.Path.GetExtension(path), System.IO.Path.GetDirectoryName(path)!,
                1, DateTime.UtcNow, cat, company is null ? null : new FileMeta { CompanyName = company });
        var rules = new List<ArchiveRule>
        {
            new() { Name = "Driver NVIDIA", Category = FileCategory.Driver, CompanyContains = "NVIDIA", Destination = System.IO.Path.Combine(archive, "Driver", "{Produttore}") },
            new() { Name = "Driver", Category = FileCategory.Driver, Destination = System.IO.Path.Combine(archive, "Driver", "Altri") },
            new() { Name = "Disattivata", Enabled = false, Destination = System.IO.Path.Combine(archive, "Mai") },
        };

        var (plan, unmatched) = RuleEngine.Plan(
            [F(a, FileCategory.Driver, "NVIDIA Corporation"), F(b, FileCategory.Driver, "NVIDIA Corporation"), F(c, FileCategory.Pdf, null)], rules);

        Assert.Equal(1, unmatched);
        Assert.Equal(2, plan.Count);
        Assert.All(plan, p => Assert.Contains(System.IO.Path.Combine("Driver", "NVIDIA"), p.Destination));
        Assert.Equal(MoveItemStatus.Rinominato, plan[1].Status); // stesso nome: il secondo diventa "(2)"
        Assert.EndsWith("nvidia-driver (2).exe", plan[1].Destination);
    }
}
