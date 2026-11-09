using PcOrg.Core.Indexing;
using PcOrg.Core.Moving;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Tests;

public class MoveTests
{
    [Fact]
    public void Il_piano_non_sovrascrive_e_rinomina_i_conflitti()
    {
        using var dir = new TempDir();
        var a = dir.File("Download/setup.exe");
        var b = dir.File("Download/sub/setup.exe");
        var already = dir.File("Archivio/altro.exe");
        dir.File("Archivio/setup.exe");
        var missing = System.IO.Path.Combine(dir.Path, "Download", "sparito.exe");

        var plan = MovePlanner.Plan([a, b, already, missing], System.IO.Path.Combine(dir.Path, "Archivio"));

        Assert.Equal(MoveItemStatus.Rinominato, plan[0].Status);
        Assert.EndsWith("setup (2).exe", plan[0].Destination);
        Assert.EndsWith("setup (3).exe", plan[1].Destination);
        Assert.Equal(MoveItemStatus.GiaPresente, plan[2].Status);
        Assert.Equal(MoveItemStatus.NonTrovato, plan[3].Status);
        // Il piano non tocca il disco
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void Sposta_aggiorna_l_indice_e_si_puo_annullare()
    {
        using var dir = new TempDir();
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var download = index.AddRoot(dir.Dir("Download"));
        var archivio = index.AddRoot(dir.Dir("Archivio/Driver"));
        var source = dir.File("Download/nvidia_driver.exe", "contenuto");
        index.SyncRoot(download.Id, FolderScanner.Enumerate(download.Path));
        var service = new MoveService(index);

        var plan = MovePlanner.Plan([source], archivio.Path);
        var result = service.Execute(plan, "Driver NVIDIA");

        var target = System.IO.Path.Combine(archivio.Path, "nvidia_driver.exe");
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.Moved);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(target));
        var indexed = Assert.Single(index.Search(new SearchQuery("nvidia")));
        Assert.Equal(target, indexed.Path);
        Assert.Equal(archivio.Id, indexed.RootId);

        var batch = Assert.Single(index.GetMoveBatches());
        Assert.Equal(1, batch.ItemCount);

        var undo = service.Undo(batch.Id);
        Assert.Empty(undo.Errors);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
        Assert.Equal(source, Assert.Single(index.Search(new SearchQuery("nvidia"))).Path);
        Assert.True(Assert.Single(index.GetMoveBatches()).Undone);
    }

    [Fact]
    public void Spostare_fuori_dalle_cartelle_monitorate_toglie_dall_indice_e_l_annullamento_lo_rimette()
    {
        using var dir = new TempDir();
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var download = index.AddRoot(dir.Dir("Download"));
        var source = dir.File("Download/tool.exe");
        index.SyncRoot(download.Id, FolderScanner.Enumerate(download.Path));
        var service = new MoveService(index);

        var result = service.Execute(MovePlanner.Plan([source], dir.Dir("Fuori")), "test");
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.Moved);
        Assert.Empty(index.Search(new SearchQuery("tool")));

        service.Undo(result.BatchId);
        Assert.Single(index.Search(new SearchQuery("tool")));
    }

    [Fact]
    public void L_annullamento_non_sovrascrive_un_file_comparso_nel_frattempo()
    {
        using var dir = new TempDir();
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var source = dir.File("Download/a.exe", "originale");
        var service = new MoveService(index);
        var result = service.Execute(MovePlanner.Plan([source], dir.Dir("Archivio")), "test");

        File.WriteAllText(source, "nuovo");
        var undo = service.Undo(result.BatchId);

        Assert.Single(undo.Errors);
        Assert.Equal("nuovo", File.ReadAllText(source));
        Assert.False(Assert.Single(index.GetMoveBatches()).Undone);
    }
}
