using PcOrg.Core.Indexing;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Tests;

/// <summary>Percorsi di rete e comportamento con cartelle non raggiungibili.</summary>
public class NetworkTests
{
    [Theory]
    [InlineData(@"\\NAS\Foto", true)]
    [InlineData(@"\\192.168.1.10\Condivisa\Driver", true)]
    [InlineData(@"//server/share", true)]
    [InlineData(@"C:\Download", false)]
    [InlineData(@"\\?\C:\Download", false)]
    [InlineData(@"\\.\PhysicalDrive0", false)]
    [InlineData(@"Download", false)]
    public void Riconosce_i_percorsi_unc(string path, bool expected)
    {
        Assert.Equal(expected, NetworkPaths.IsUnc(path));
        Assert.Equal(expected, NetworkPaths.IsNetworkPath(path));
    }

    [Fact]
    public void Un_percorso_locale_non_viene_convertito()
    {
        Assert.Equal(@"C:\Download", NetworkPaths.ToUnc(@"C:\Download"));
        Assert.Equal(@"\\NAS\Foto", NetworkPaths.ToUnc(@"\\NAS\Foto"));
    }

    [Fact]
    public async Task Raggiungibilita_con_tempo_massimo()
    {
        using var dir = new TempDir();
        Assert.True(await NetworkPaths.IsReachableAsync(dir.Path, TimeSpan.FromSeconds(5)));
        Assert.False(await NetworkPaths.IsReachableAsync(System.IO.Path.Combine(dir.Path, "manca"), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Se_la_cartella_sparisce_durante_la_scansione_l_indice_resta_invariato()
    {
        using var dir = new TempDir();
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var root = index.AddRoot(dir.Dir("NAS"));
        dir.File("NAS/a.exe");
        dir.File("NAS/b.pdf");
        index.SyncRoot(root.Id, FolderScanner.Enumerate(root.Path));

        // Scansione parziale (la rete è caduta a metà) e cartella non più disponibile
        var partial = FolderScanner.Enumerate(root.Path).Take(1).ToList();
        Assert.Throws<IOException>(() => index.SyncRoot(root.Id, partial, isStillAvailable: () => false));

        Assert.Equal(2, index.Search(new SearchQuery(null)).Count);
    }
}
