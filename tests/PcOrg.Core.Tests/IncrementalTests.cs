using PcOrg.Core.Classification;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Indexing;
using PcOrg.Core.Metadata;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Tests;

/// <summary>Aggiornamento incrementale: si rilegge solo ciò che è cambiato.</summary>
public class IncrementalTests
{
    /// <summary>Le date delle cartelle hanno una precisione limitata: si lascia passare un attimo.</summary>
    private static void Tick() => Thread.Sleep(30);

    private static (FileIndex Index, RootFolder Root, TempDir Dir) Library()
    {
        var dir = new TempDir();
        for (var d = 0; d < 20; d++)
            for (var f = 0; f < 10; f++)
                dir.File($"Archivio/Cartella{d:D2}/file{f}.txt");
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var root = index.AddRoot(dir.Dir("Archivio"));
        var first = index.UpdateRoot(root.Id, root.Path);
        Assert.Equal(200, first.FilesAdded);
        Assert.Equal(21, first.DirectoriesChanged);
        return (index, root, dir);
    }

    [Fact]
    public void Senza_modifiche_non_rilegge_nessuna_cartella()
    {
        var (index, root, dir) = Library();
        using (dir)
        {
            var again = index.UpdateRoot(root.Id, root.Path);
            Assert.Equal(21, again.DirectoriesVisited);
            Assert.Equal(0, again.DirectoriesChanged);
            Assert.Equal(0, again.FilesChanged);
        }
    }

    [Fact]
    public void Un_file_aggiunto_fa_rileggere_solo_la_sua_cartella()
    {
        var (index, root, dir) = Library();
        using (dir)
        {
            Tick();
            dir.File("Archivio/Cartella07/nuovo-driver.exe");
            var result = index.UpdateRoot(root.Id, root.Path);

            Assert.Equal(1, result.DirectoriesChanged);
            Assert.Equal(1, result.FilesAdded);
            Assert.Equal(0, result.FilesRemoved);
            Assert.Single(index.Search(new SearchQuery("nuovo-driver")));
            Assert.Equal(201, index.GetStats().Files);
        }
    }

    [Fact]
    public void File_e_cartelle_eliminati_o_rinominati()
    {
        var (index, root, dir) = Library();
        using (dir)
        {
            Tick();
            Directory.Delete(System.IO.Path.Combine(root.Path, "Cartella03"), recursive: true);
            File.Move(System.IO.Path.Combine(root.Path, "Cartella05", "file1.txt"),
                      System.IO.Path.Combine(root.Path, "Cartella05", "rinominato.txt"));
            Directory.Move(System.IO.Path.Combine(root.Path, "Cartella09"), System.IO.Path.Combine(root.Path, "Cartella09-bis"));

            var result = index.UpdateRoot(root.Id, root.Path);

            Assert.Equal(190, index.GetStats().Files);
            Assert.Empty(index.Search(new SearchQuery("Cartella03")));
            Assert.Single(index.Search(new SearchQuery("rinominato")));
            Assert.Equal(10, index.Search(new SearchQuery("Cartella09-bis")).Count);
            Assert.True(result.DirectoriesChanged <= 3, $"Cartelle rilette: {result.DirectoriesChanged}");
        }
    }

    [Fact]
    public void La_scansione_completa_rileva_anche_il_contenuto_modificato()
    {
        var (index, root, dir) = Library();
        using (dir)
        {
            var path = System.IO.Path.Combine(root.Path, "Cartella01", "file0.txt");
            var folderTime = Directory.GetLastWriteTimeUtc(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "contenuto molto più lungo di prima");
            Directory.SetLastWriteTimeUtc(System.IO.Path.GetDirectoryName(path)!, folderTime); // come su NTFS

            Assert.Equal(0, index.UpdateRoot(root.Id, root.Path).FilesUpdated); // limite noto dell'aggiornamento rapido
            Assert.Equal(1, index.UpdateRoot(root.Id, root.Path, full: true).FilesUpdated);
        }
    }

    [Fact]
    public void Le_modifiche_segnalate_si_applicano_una_per_una()
    {
        var (index, root, dir) = Library();
        using (dir)
        {
            var added = dir.File("Archivio/Cartella02/setup-vnc.exe");
            var newFolder = dir.Dir("Archivio/Nuova");
            dir.File("Archivio/Nuova/a.pdf");
            dir.File("Archivio/Nuova/Sotto/b.pdf");
            var deleted = System.IO.Path.Combine(root.Path, "Cartella04", "file3.txt");
            File.Delete(deleted);

            var result = index.ApplyChanges([(root.Id, added), (root.Id, newFolder), (root.Id, deleted)]);

            Assert.Equal(3, result.FilesAdded);
            Assert.Equal(1, result.FilesRemoved);
            Assert.Equal(202, index.GetStats().Files);

            // Cartella eliminata segnalata dall'osservatore
            Directory.Delete(newFolder, recursive: true);
            Assert.Equal(2, index.ApplyChanges([(root.Id, newFolder)]).FilesRemoved);
        }
    }

    [Fact]
    public async Task L_osservatore_segnala_un_file_nuovo()
    {
        using var dir = new TempDir();
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var root = index.AddRoot(dir.Dir("Download"));
        FolderWatcher.Quiet = TimeSpan.FromMilliseconds(200);
        using var watcher = new FolderWatcher();
        var received = new TaskCompletionSource<IReadOnlyList<(long RootId, string Path)>>();
        watcher.ChangesReady += batch => received.TrySetResult(batch);
        watcher.Watch(index.GetRoots());

        var path = dir.File("Download/appena-scaricato.zip");

        var finished = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(finished == received.Task, "Nessuna modifica segnalata");
        var changes = await received.Task;
        Assert.Contains(changes, c => c.Path == path && c.RootId == root.Id);
        index.ApplyChanges(changes);
        Assert.Single(index.Search(new SearchQuery("appena-scaricato")));
    }

    [Theory]
    [InlineData("[connection]\nhost=192.168.1.20\nport=5900\n", ".vnc", "Connessione VNC a 192.168.1.20:5900")]
    [InlineData("ConnMethod=tcp\nHost=ufficio.local:5901\nUserName=mario\n", ".vnc", "Connessione VNC a ufficio.local:5901")]
    [InlineData("screen mode id:i:2\nfull address:s:server01\nusername:s:ADMIN\n", ".rdp", "Connessione Desktop remoto a server01 (utente ADMIN)")]
    public void Legge_l_indirizzo_delle_connessioni_remote(string text, string ext, string expected)
    {
        Assert.Equal(expected, RemoteConnectionReader.Parse(text, ext)!.FileDescription);
    }

    [Fact]
    public void I_file_vnc_sono_nella_categoria_accesso_remoto()
    {
        Assert.Equal(FileCategory.AccessoRemoto, FileClassifier.Classify(@"C:\Connessioni\Ufficio.vnc"));
        Assert.Equal(FileCategory.AccessoRemoto, FileClassifier.Classify(@"C:\Connessioni\Server.RDP"));
        Assert.Equal("VNC (.vnc)", Subcategories.Find(".vnc")?.Name);

        using var dir = new TempDir();
        var path = dir.File("Remoti/ufficio.vnc", "[connection]\r\nhost=10.0.0.5\r\nport=5900\r\n");
        var meta = MetadataExtractor.Extract(path);
        Assert.Equal("Connessione VNC a 10.0.0.5:5900", meta?.FileDescription);
    }

    [Fact]
    public void Il_registro_si_puo_disattivare_e_filtrare()
    {
        using var dir = new TempDir();
        Log.Initialize(System.IO.Path.Combine(dir.Path, "log"));
        try
        {
            Log.Configure(enabled: false, LogLevel.Info);
            Log.Error("non deve comparire");
            Assert.False(File.Exists(Log.FilePath));

            Log.Configure(enabled: true, LogLevel.Warning);
            Log.Info("informazione filtrata");
            Log.Warn("avviso registrato");
            var text = File.ReadAllText(Log.FilePath!);
            Assert.DoesNotContain("informazione filtrata", text);
            Assert.Contains("avviso registrato", text);

            Log.Clear();
            Assert.False(File.Exists(Log.FilePath));
        }
        finally
        {
            Log.Configure(enabled: true, LogLevel.Info);
            Log.Initialize(System.IO.Path.Combine(dir.Path, "log2"));
        }
    }
}
