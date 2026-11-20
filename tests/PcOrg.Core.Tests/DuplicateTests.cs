using PcOrg.Core.Duplicates;
using PcOrg.Core.Indexing;
using PcOrg.Core.Moving;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Tests;

/// <summary>Ricerca dei cloni, regole di pulizia e Cestino.</summary>
public class DuplicateTests
{
    private static (FileIndex Index, TempDir Dir) Library()
    {
        var dir = new TempDir();
        var big = new string('A', 200_000);
        dir.File("Download/setup.exe", big);
        dir.File("Download/copia di setup.exe", big);          // clone, nome diverso
        dir.File("Archivio/Installer/setup.exe", big);         // clone, altra cartella
        dir.File("Download/altro.exe", new string('B', 200_000)); // stessa dimensione, contenuto diverso
        var nearly = big.ToCharArray();
        nearly[100_000] = 'Z';                                 // differisce solo a metà: serve il confronto completo
        dir.File("Download/quasi-uguale.exe", new string(nearly));
        dir.File("Download/nota.txt", "ciao");
        dir.File("Download/nota-copia.txt", "ciao");          // clone piccolo
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        foreach (var folder in new[] { "Download", "Archivio" })
        {
            var root = index.AddRoot(dir.Dir(folder));
            index.UpdateRoot(root.Id, root.Path);
        }
        return (index, dir);
    }

    [Fact]
    public void Trova_i_file_identici_anche_con_nomi_diversi()
    {
        var (index, dir) = Library();
        using (dir)
        {
            var groups = DuplicateFinder.Find(index, new DuplicateOptions());

            Assert.Equal(2, groups.Count);
            var setup = groups[0];
            Assert.Equal(3, setup.Files.Count);
            Assert.Equal(400_000, setup.WastedBytes);
            Assert.DoesNotContain(setup.Files, f => f.Name is "altro.exe" or "quasi-uguale.exe");
            Assert.Equal(2, groups[1].Files.Count);
        }
    }

    [Fact]
    public void La_dimensione_minima_esclude_i_file_piccoli()
    {
        var (index, dir) = Library();
        using (dir)
            Assert.Single(DuplicateFinder.Find(index, new DuplicateOptions(MinSize: 1024)));
    }

    [Fact]
    public void Le_impronte_vengono_riusate_alla_ricerca_successiva()
    {
        var (index, dir) = Library();
        using (dir)
        {
            DuplicateFinder.Find(index, new DuplicateOptions());
            Assert.True(DuplicateFinder.LastComputed > 0);

            var again = DuplicateFinder.Find(index, new DuplicateOptions());
            Assert.Equal(0, DuplicateFinder.LastComputed);
            Assert.Equal(2, again.Count);
        }
    }

    [Fact]
    public void Le_regole_lasciano_sempre_una_copia()
    {
        var (index, dir) = Library();
        using (dir)
        {
            var groups = DuplicateFinder.Find(index, new DuplicateOptions());
            foreach (var rule in Enum.GetValues<KeepRule>())
            {
                var doubles = CleanupRules.Doubles(groups, rule).Select(f => f.Id).ToHashSet();
                Assert.Equal(3, doubles.Count); // 2 copie di setup + 1 di nota
                Assert.Empty(CleanupRules.GroupsLosingAllCopies(groups, doubles));
            }
            var shortest = CleanupRules.Keeper(groups[0], KeepRule.PercorsoPiuCorto);
            Assert.Equal("setup.exe", shortest.Name);
            var all = groups.SelectMany(g => g.Files).Select(f => f.Id).ToHashSet();
            Assert.Equal(2, CleanupRules.GroupsLosingAllCopies(groups, all).Count);
        }
    }

    [Fact]
    public void Il_cestino_sposta_solo_file_invariati_e_aggiorna_l_indice()
    {
        var (index, dir) = Library();
        using (dir)
        {
            var original = RecycleBin.SendToRecycleBin;
            RecycleBin.SendToRecycleBin = path => { File.Delete(path); return true; }; // simulazione del Cestino
            try
            {
                var groups = DuplicateFinder.Find(index, new DuplicateOptions());
                var doubles = CleanupRules.Doubles(groups, KeepRule.PiuVecchio).ToList();
                var changed = doubles[0];
                File.AppendAllText(changed.Path, "modificato"); // cambiato dopo la ricerca: non va toccato

                var result = RecycleBin.Recycle(index, doubles);

                Assert.Equal(doubles.Count - 1, result.Recycled.Count);
                Assert.Single(result.Errors);
                Assert.True(File.Exists(changed.Path));
                Assert.Equal(7 - result.Recycled.Count, index.GetStats().Files);
            }
            finally
            {
                RecycleBin.SendToRecycleBin = original;
            }
        }
    }
}

public class CleanupRulesTests
{
    private static Storage.FileRecord F(long id, string path, DateTime modified) =>
        new(id, 1, path, Path.GetFileName(path), ".zip", Path.GetDirectoryName(path) ?? "", 100, modified, Classification.FileCategory.Archivio);

    private static readonly DateTime Jan = new(2024, 1, 10, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Mar = new(2024, 3, 5, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Di_norma_si_tiene_la_copia_piu_recente_e_si_eliminano_le_piu_vecchie()
    {
        Assert.Equal(KeepRule.PiuRecente, CleanupRules.DefaultRule);
        Assert.Equal(KeepRule.PiuRecente, CleanupRules.Ordered[0]);
        var group = new DuplicateGroup("h", 100, [F(1, @"C:\a\x.zip", Jan), F(2, @"C:\b\x.zip", Mar), F(3, @"C:\c\x.zip", Jan.AddDays(1))]);
        Assert.Equal(2, CleanupRules.Keeper(group, CleanupRules.DefaultRule).Id);
        Assert.Equal([1L, 3L], CleanupRules.Doubles([group], CleanupRules.DefaultRule).Select(f => f.Id).Order());
        Assert.Equal(1, CleanupRules.Keeper(group, KeepRule.PiuVecchio).Id);
    }

    [Fact]
    public void A_parita_di_data_di_modifica_decide_la_data_di_creazione()
    {
        // Copiando un file Windows mantiene la data di modifica: le copie hanno la stessa data.
        var original = CleanupRules.CreationTime;
        try
        {
            var created = new Dictionary<string, DateTime>
            {
                [@"C:\originale\x.zip"] = Jan,
                [@"C:\copia\x.zip"] = Mar, // creata dopo: è la copia più recente
            };
            CleanupRules.CreationTime = p => created.TryGetValue(p, out var d) ? d : null;
            var group = new DuplicateGroup("h", 100, [F(1, @"C:\copia\x.zip", Jan), F(2, @"C:\originale\x.zip", Jan)]);
            Assert.Equal(1, CleanupRules.Keeper(group, KeepRule.PiuRecente).Id);
            Assert.Equal(2, CleanupRules.Keeper(group, KeepRule.PiuVecchio).Id);
            Assert.Equal(2, Assert.Single(CleanupRules.Doubles([group], KeepRule.PiuRecente)).Id); // si elimina l'originale più vecchio
        }
        finally
        {
            CleanupRules.CreationTime = original;
        }
    }

    [Fact]
    public void Senza_date_distinte_la_scelta_e_comunque_stabile()
    {
        var original = CleanupRules.CreationTime;
        try
        {
            CleanupRules.CreationTime = _ => null;
            var a = F(1, @"C:\uno\due\x.zip", Jan);
            var b = F(2, @"C:\uno\x.zip", Jan);
            Assert.Equal(2, CleanupRules.Keeper(new DuplicateGroup("h", 100, [a, b]), KeepRule.PiuRecente).Id);
            Assert.Equal(2, CleanupRules.Keeper(new DuplicateGroup("h", 100, [b, a]), KeepRule.PiuRecente).Id); // ordine indifferente
        }
        finally
        {
            CleanupRules.CreationTime = original;
        }
    }

    [Fact]
    public void Non_si_eliminano_mai_tutte_le_copie()
    {
        var group = new DuplicateGroup("h", 100, [F(1, @"C:\a\x.zip", Jan), F(2, @"C:\b\x.zip", Mar)]);
        Assert.Single(CleanupRules.GroupsLosingAllCopies([group], new HashSet<long> { 1, 2 }));
        Assert.Empty(CleanupRules.GroupsLosingAllCopies([group], new HashSet<long> { 1 }));
    }
}
