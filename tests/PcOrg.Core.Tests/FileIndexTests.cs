using PcOrg.Core.Classification;
using PcOrg.Core.Indexing;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Tests;

public class FileIndexTests
{
    private static (FileIndex Index, RootFolder Root) Indexed(TempDir dir, string folder = "Download")
    {
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "db", "index.db"));
        var root = index.AddRoot(dir.Dir(folder));
        index.SyncRoot(root.Id, FolderScanner.Enumerate(root.Path));
        return (index, root);
    }

    [Fact]
    public void Scansione_e_ricerca_per_parte_del_nome()
    {
        using var dir = new TempDir();
        dir.File("Download/ChromeSetup.exe");
        dir.File("Download/Drivers/nvidia_566.36_win11.exe");
        dir.File("Download/manuale.pdf");
        var (index, _) = Indexed(dir);

        Assert.Single(index.Search(new SearchQuery("setup")));
        Assert.Equal("nvidia_566.36_win11.exe", Assert.Single(index.Search(new SearchQuery("vidia 566"))).Name);
        // Ricerca anche sul nome della cartella
        Assert.Single(index.Search(new SearchQuery("drivers")));
        // Termini corti (< 3 caratteri) gestiti con LIKE
        Assert.Single(index.Search(new SearchQuery("ma pdf")));
        Assert.Equal(3, index.Search(new SearchQuery("")).Count);
    }

    [Fact]
    public void Filtro_per_categoria_e_statistiche()
    {
        using var dir = new TempDir();
        dir.File("Download/a.iso", "1234");
        dir.File("Download/b.iso", "12");
        dir.File("Download/c.msi");
        var (index, _) = Indexed(dir);

        Assert.Equal(2, index.Search(new SearchQuery(null, FileCategory.ImmagineDisco)).Count);
        var iso = index.GetCategoryStats().Single(s => s.Category == FileCategory.ImmagineDisco);
        Assert.Equal(2, iso.Count);
        Assert.Equal(6, iso.TotalSize);
    }

    [Fact]
    public void Una_nuova_scansione_rimuove_i_file_spariti()
    {
        using var dir = new TempDir();
        var gone = dir.File("Download/vecchio.exe");
        dir.File("Download/resta.exe");
        var (index, root) = Indexed(dir);

        File.Delete(gone);
        dir.File("Download/nuovo.exe");
        index.SyncRoot(root.Id, FolderScanner.Enumerate(root.Path));

        var names = index.Search(new SearchQuery(null)).Select(f => f.Name).Order().ToArray();
        Assert.Equal(["nuovo.exe", "resta.exe"], names);
        Assert.Empty(index.Search(new SearchQuery("vecchio")));
    }

    [Fact]
    public void Cartelle_annidate()
    {
        using var dir = new TempDir();
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var child = index.AddRoot(dir.Dir("A/B"));

        Assert.Throws<InvalidOperationException>(() => index.AddRoot(dir.Dir("A/B/C")));
        Assert.Throws<InvalidOperationException>(() => index.AddRoot(child.Path));

        // Una cartella che contiene una già monitorata la assorbe
        var parent = index.AddRoot(dir.Dir("A"));
        Assert.Equal(parent.Path, Assert.Single(index.GetRoots()).Path);
    }

    [Fact]
    public void Rimuovere_una_cartella_toglie_i_file_dall_indice_ma_non_dal_disco()
    {
        using var dir = new TempDir();
        var file = dir.File("Download/x.exe");
        var (index, root) = Indexed(dir);

        index.RemoveRoot(root.Id);

        Assert.Empty(index.Search(new SearchQuery(null)));
        Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData("setup", "\"setup\"")]
    [InlineData("nvidia  566", "\"nvidia\" AND \"566\"")]
    [InlineData("a\"b\"c", "\"a\"\"b\"\"c\"")]
    public void Query_fts(string input, string expected)
    {
        Assert.Equal(expected, FileIndex.BuildTextFilter(input).Match);
    }
}
