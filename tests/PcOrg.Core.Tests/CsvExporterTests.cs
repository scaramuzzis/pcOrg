using PcOrg.Core.Classification;
using PcOrg.Core.Metadata;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Tests;

public class CsvExporterTests
{
    [Fact]
    public void Esporta_intestazione_e_righe_con_escape()
    {
        var file = new FileRecord(1, 1, @"C:\Download\a;b.exe", "a;b.exe", ".exe", @"C:\Download", 1234,
            new DateTime(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc), FileCategory.Installer,
            new FileMeta { ProductVersion = "1.2", CompanyName = "Ditta \"Bella\"", Signer = "Ditta" });
        using var writer = new StringWriter();

        CsvExporter.Write([file], writer);

        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Nome;Categoria;Versione;Produttore", lines[0]);
        Assert.StartsWith("\"a;b.exe\";Installer;1.2;\"Ditta \"\"Bella\"\"\";", lines[1]);
        Assert.Contains(";1234;", lines[1]);
    }

    [Fact]
    public void Il_file_ha_il_bom_per_excel()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "elenco.csv");
        CsvExporter.Write([], path);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
    }
}
