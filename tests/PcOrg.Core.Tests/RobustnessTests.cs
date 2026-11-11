using PcOrg.Core.Diagnostics;
using PcOrg.Core.Indexing;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Tests;

/// <summary>Persistenza dei dati: scansioni interrotte, riapertura, manutenzione, registro.</summary>
public class RobustnessTests
{
    /// <summary>Simula una scansione interrotta (es. app chiusa) dopo un certo numero di file.</summary>
    private static IEnumerable<ScannedFile> InterruptedAfter(IEnumerable<ScannedFile> files, int count)
    {
        var n = 0;
        foreach (var file in files)
        {
            if (n++ == count)
                throw new OperationCanceledException();
            yield return file;
        }
    }

    [Fact]
    public void Una_scansione_interrotta_conserva_i_file_gia_trovati()
    {
        using var dir = new TempDir();
        var total = FileIndex.ScanBatchSize + 500;
        var folder = dir.Dir("Download");
        for (var i = 0; i < total; i++)
            File.WriteAllText(System.IO.Path.Combine(folder, $"file{i:D5}.txt"), "");
        var dbPath = System.IO.Path.Combine(dir.Path, "index.db");
        var index = FileIndex.Open(dbPath);
        var root = index.AddRoot(folder);

        var stopAt = FileIndex.ScanBatchSize + 100;
        Assert.Throws<OperationCanceledException>(() =>
            index.SyncRoot(root.Id, InterruptedAfter(FolderScanner.Enumerate(root.Path), stopAt)));

        // "Riavvio": si riapre l'indice dallo stesso file
        var reopened = FileIndex.Open(dbPath);
        Assert.Equal(stopAt, reopened.GetStats().Files);

        // La scansione successiva completa l'elenco
        reopened.SyncRoot(root.Id, FolderScanner.Enumerate(root.Path));
        Assert.Equal(total, reopened.GetStats().Files);
    }

    [Fact]
    public void Una_scansione_interrotta_non_cancella_i_file_non_ancora_visti()
    {
        using var dir = new TempDir();
        var folder = dir.Dir("Download");
        for (var i = 0; i < 50; i++)
            File.WriteAllText(System.IO.Path.Combine(folder, $"f{i:D2}.txt"), "");
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var root = index.AddRoot(folder);
        index.SyncRoot(root.Id, FolderScanner.Enumerate(root.Path));

        Assert.Throws<OperationCanceledException>(() =>
            index.SyncRoot(root.Id, InterruptedAfter(FolderScanner.Enumerate(root.Path), 10)));

        Assert.Equal(50, index.GetStats().Files);
    }

    [Fact]
    public void I_dati_restano_dopo_la_riapertura()
    {
        using var dir = new TempDir();
        dir.File("Download/setup.exe");
        var dbPath = System.IO.Path.Combine(dir.Path, "dati", "indice.db");
        var index = FileIndex.Open(dbPath);
        var root = index.AddRoot(dir.Dir("Download"));
        index.SyncRoot(root.Id, FolderScanner.Enumerate(root.Path));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // come alla chiusura del programma

        var reopened = FileIndex.Open(dbPath);
        Assert.Single(reopened.GetRoots());
        Assert.Equal("setup.exe", Assert.Single(reopened.Search(new SearchQuery(null))).Name);
        Assert.Equal(System.IO.Path.GetFullPath(dbPath), reopened.GetStats().DatabasePath);
    }

    [Fact]
    public void Svuota_e_compatta_l_indice()
    {
        using var dir = new TempDir();
        dir.File("Download/a.pdf");
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var root = index.AddRoot(dir.Dir("Download"));
        index.SyncRoot(root.Id, FolderScanner.Enumerate(root.Path));

        index.ClearFiles();
        index.Compact();

        var stats = index.GetStats();
        Assert.Equal(0, stats.Files);
        Assert.Equal(1, stats.Roots);
        Assert.Null(Assert.Single(index.GetRoots()).LastScanUtc);
    }

    [Fact]
    public void Il_registro_scrive_su_file()
    {
        using var dir = new TempDir();
        Log.Initialize(System.IO.Path.Combine(dir.Path, "log"));
        try
        {
            Log.Info("avvio di prova");
            Log.Error("errore di prova", new InvalidOperationException("dettaglio"));
            var text = File.ReadAllText(Log.FilePath!);
            Assert.Contains("[INFO] avvio di prova", text);
            Assert.Contains("[ERRORE] errore di prova", text);
            Assert.Contains("dettaglio", text);
        }
        finally
        {
            Log.Initialize(System.IO.Path.Combine(dir.Path, "log2"));
        }
    }

    [Fact]
    public void La_ricerca_usa_la_cartella_relativa_e_resta_corretta_dopo_spostamenti_e_unione_di_cartelle()
    {
        using var dir = new TempDir();
        dir.File("Archivio/Driver/NVIDIA/setup.exe");
        dir.File("Archivio/Documenti/nota.txt");
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var child = index.AddRoot(System.IO.Path.Combine(dir.Path, "Archivio", "Driver"));
        index.UpdateRoot(child.Id, child.Path);

        // La sottocartella si trova; il nome della cartella monitorata non fa comparire tutti i suoi file
        Assert.Single(index.Search(new SearchQuery("nvidia")));
        Assert.Empty(index.Search(new SearchQuery("archivio")));

        // Unendo la cartella in una più grande, la cartella relativa viene ricalcolata
        var parent = index.AddRoot(System.IO.Path.Combine(dir.Path, "Archivio"));
        index.UpdateRoot(parent.Id, parent.Path);
        Assert.Single(index.Search(new SearchQuery("driver nvidia")));
        Assert.Single(index.Search(new SearchQuery("documenti")));

        // Dopo uno spostamento la ricerca per cartella segue il file
        var from = System.IO.Path.Combine(parent.Path, "Driver", "NVIDIA", "setup.exe");
        var target = dir.Dir("Archivio/Vecchi");
        new PcOrg.Core.Moving.MoveService(index).Execute(PcOrg.Core.Moving.MovePlanner.Plan([from], target), "test");
        Assert.Empty(index.Search(new SearchQuery("nvidia")));
        Assert.Equal("setup.exe", Assert.Single(index.Search(new SearchQuery("vecchi"))).Name);
    }
}
