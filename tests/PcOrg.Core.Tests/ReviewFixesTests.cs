using PcOrg.Core.Indexing;
using PcOrg.Core.Moving;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Tests;

/// <summary>Correzioni emerse dalla revisione: collegamenti a cartelle, file solo online, CSV, Cestino reale.</summary>
public class ReviewFixesTests
{
    [Fact]
    public void I_collegamenti_a_cartelle_non_vengono_seguiti()
    {
        using var dir = new TempDir();
        dir.File("Radice/Vera/file.txt");
        var link = System.IO.Path.Combine(dir.Path, "Radice", "Collegamento");
        try
        {
            Directory.CreateSymbolicLink(link, System.IO.Path.Combine(dir.Path, "Radice", "Vera"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // su Windows servono privilegi per creare collegamenti: il test non si applica
        }
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var root = index.AddRoot(System.IO.Path.Combine(dir.Path, "Radice"));

        index.UpdateRoot(root.Id, root.Path);

        Assert.Equal(1, index.GetStats().Files); // il file compare una volta sola
        Assert.Single(FolderScanner.Enumerate(root.Path));
    }

    [Theory]
    [InlineData(FileAttributes.Normal, false)]
    [InlineData(FileAttributes.Archive | FileAttributes.ReparsePoint, false)] // file OneDrive già scaricato
    [InlineData(FileAttributes.Offline, true)]
    [InlineData((FileAttributes)0x00400000, true)] // RecallOnDataAccess: solo online
    [InlineData((FileAttributes)0x00040000, true)] // RecallOnOpen
    public void Riconosce_i_file_solo_online(FileAttributes attributes, bool expected)
    {
        Assert.Equal(expected, CloudFiles.IsOnlineOnly(attributes));
    }

    [Theory]
    [InlineData("=cmd|' /C calc'!A0", "'=cmd|' /C calc'!A0")]
    [InlineData("-setup.exe", "'-setup.exe")]
    [InlineData("@file", "'@file")]
    [InlineData("normale.exe", "normale.exe")]
    public void Il_csv_neutralizza_le_formule(string value, string expected)
    {
        Assert.Equal(expected, CsvExporter.Escape(value));
    }

    [Fact]
    public void L_aggiornamento_di_una_sottocartella_non_tocca_le_altre()
    {
        using var dir = new TempDir();
        dir.File("Radice/A/uno.txt");
        dir.File("Radice/AB/due.txt"); // nome con lo stesso prefisso di "A": non deve essere confusa
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var root = index.AddRoot(System.IO.Path.Combine(dir.Path, "Radice"));
        index.UpdateRoot(root.Id, root.Path);

        Directory.Delete(System.IO.Path.Combine(root.Path, "A"), recursive: true);
        index.ApplyChanges([(root.Id, System.IO.Path.Combine(root.Path, "A"))]);

        Assert.Equal("due.txt", Assert.Single(index.Search(new SearchQuery(null))).Name);
    }

    [Fact]
    public void Il_cestino_di_windows_funziona_davvero()
    {
        if (!OperatingSystem.IsWindows())
            return; // il Cestino esiste solo su Windows: qui si verifica la chiamata reale al sistema
        using var dir = new TempDir();
        var path = dir.File("Download/da-cestinare.txt", "prova");
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var root = index.AddRoot(dir.Dir("Download"));
        index.UpdateRoot(root.Id, root.Path);

        var result = RecycleBin.Recycle(index, index.Search(new SearchQuery(null)));

        Assert.Empty(result.Errors);
        Assert.Single(result.Recycled);
        Assert.False(File.Exists(path));
        Assert.Equal(0, index.GetStats().Files);
    }
}
