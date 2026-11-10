using PcOrg.Core.Classification;
using PcOrg.Core.Indexing;
using PcOrg.Core.Metadata;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Tests;

/// <summary>Indice: scansione, ricerca, categorie, cartelle annidate.</summary>
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
        Assert.Single(index.Search(new SearchQuery(null) { Extensions = [".msi"] }));
        Assert.Equal(new ExtensionStat(".iso", 2, 6), Assert.Single(index.GetExtensionStats(FileCategory.ImmagineDisco)));
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

/// <summary>Indice: salvataggio dei metadati e aggiornamento da database di versioni precedenti.</summary>
public class FileIndexMetadataTests
{
    [Fact]
    public void Salva_i_metadati_cerca_per_produttore_e_affina_la_categoria()
    {
        using var dir = new TempDir();
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var root = index.AddRoot(dir.Dir("Download"));
        dir.File("Download/x123.exe");
        dir.File("Download/nota.txt");
        index.SyncRoot(root.Id, FolderScanner.Enumerate(root.Path));

        var pending = Assert.Single(index.GetFilesNeedingMetadata());
        Assert.Equal(1, index.CountFilesNeedingMetadata());
        var meta = new FileMeta { ProductName = "Realtek Audio", CompanyName = "Realtek Semiconductor", FileDescription = "Realtek audio driver", ProductVersion = "6.0.9", ProductKey = "realtek audio|realtek" };
        index.SaveMetadata([(pending, meta)]);

        Assert.Empty(index.GetFilesNeedingMetadata());
        var found = Assert.Single(index.Search(new SearchQuery("semiconductor")));
        Assert.Equal(FileCategory.Driver, found.Category);
        Assert.Equal("6.0.9", found.Version);
        Assert.Single(index.GetProductFiles());

        // Una nuova scansione del file invariato non perde la categoria affinata
        index.SyncRoot(root.Id, FolderScanner.Enumerate(root.Path));
        Assert.Equal(FileCategory.Driver, Assert.Single(index.Search(new SearchQuery("x123"))).Category);
    }

    [Fact]
    public void Un_file_modificato_viene_riletto()
    {
        using var dir = new TempDir();
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var root = index.AddRoot(dir.Dir("Download"));
        var path = dir.File("Download/a.exe", "uno");
        index.SyncRoot(root.Id, FolderScanner.Enumerate(root.Path));
        index.SaveMetadata([(index.GetFilesNeedingMetadata()[0], null)]);
        Assert.Empty(index.GetFilesNeedingMetadata());

        File.WriteAllText(path, "contenuto più lungo");
        index.SyncRoot(root.Id, FolderScanner.Enumerate(root.Path));
        Assert.Single(index.GetFilesNeedingMetadata());
    }

    [Fact]
    public void Aggiornamento_da_un_database_della_versione_0_1()
    {
        using var dir = new TempDir();
        var dbPath = System.IO.Path.Combine(dir.Path, "vecchio.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE roots (id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE, last_scan_utc TEXT);
                CREATE TABLE files (id INTEGER PRIMARY KEY, root_id INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
                    path TEXT NOT NULL UNIQUE COLLATE NOCASE, name TEXT NOT NULL, ext TEXT NOT NULL, folder TEXT NOT NULL,
                    size INTEGER NOT NULL, modified_utc INTEGER NOT NULL, category INTEGER NOT NULL, seen INTEGER NOT NULL DEFAULT 0);
                CREATE VIRTUAL TABLE files_fts USING fts5(name, folder, content='files', content_rowid='id', tokenize='trigram');
                CREATE TRIGGER files_ai AFTER INSERT ON files BEGIN
                    INSERT INTO files_fts(rowid, name, folder) VALUES (new.id, new.name, new.folder); END;
                CREATE TRIGGER files_ad AFTER DELETE ON files BEGIN
                    INSERT INTO files_fts(files_fts, rowid, name, folder) VALUES ('delete', old.id, old.name, old.folder); END;
                CREATE TRIGGER files_au AFTER UPDATE OF name, folder ON files BEGIN
                    INSERT INTO files_fts(files_fts, rowid, name, folder) VALUES ('delete', old.id, old.name, old.folder);
                    INSERT INTO files_fts(rowid, name, folder) VALUES (new.id, new.name, new.folder); END;
                CREATE TABLE move_batches (id INTEGER PRIMARY KEY, created_utc TEXT NOT NULL, description TEXT NOT NULL, undone INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE move_log (id INTEGER PRIMARY KEY, batch_id INTEGER NOT NULL REFERENCES move_batches(id) ON DELETE CASCADE,
                    from_path TEXT NOT NULL, to_path TEXT NOT NULL);
                INSERT INTO roots(id, path) VALUES (1, 'C:\Download');
                INSERT INTO files(root_id, path, name, ext, folder, size, modified_utc, category)
                    VALUES (1, 'C:\Download\nvidia-setup.exe', 'nvidia-setup.exe', '.exe', 'C:\Download', 10, 0, 1);
                INSERT INTO files(root_id, path, name, ext, folder, size, modified_utc, category)
                    VALUES (1, 'C:\Download\manuale.pdf', 'manuale.pdf', '.pdf', 'C:\Download', 10, 0, 6);
                INSERT INTO files(root_id, path, name, ext, folder, size, modified_utc, category)
                    VALUES (1, 'C:\Download\note.txt', 'note.txt', '.txt', 'C:\Download', 10, 0, 6);
                INSERT INTO files(root_id, path, name, ext, folder, size, modified_utc, category)
                    VALUES (1, 'C:\Download\pc.gho', 'pc.gho', '.gho', 'C:\Download', 10, 0, 0);
                PRAGMA user_version = 1;
                """;
            cmd.ExecuteNonQuery();
        }

        var index = FileIndex.Open(dbPath);

        Assert.Single(index.Search(new SearchQuery("nvidia")));
        Assert.Single(index.GetFilesNeedingMetadata());
        // v3: i documenti esistenti passano alle nuove categorie
        Assert.Equal(FileCategory.Pdf, Assert.Single(index.Search(new SearchQuery("manuale"))).Category);
        Assert.Equal(FileCategory.Testo, Assert.Single(index.Search(new SearchQuery("note.txt"))).Category);
        Assert.Empty(index.Search(new SearchQuery(null, FileCategory.Documento)));
        // v4: le immagini Ghost (prima "Altro") passano a Immagini disco
        Assert.Equal(FileCategory.ImmagineDisco, Assert.Single(index.Search(new SearchQuery("pc.gho"))).Category);
    }
}
