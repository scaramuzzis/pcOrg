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
