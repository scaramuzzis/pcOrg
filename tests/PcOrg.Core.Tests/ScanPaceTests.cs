using System.Diagnostics;

namespace PcOrg.Core.Tests;

public class ScanPaceTests
{
    [Fact]
    public void La_velocita_leggera_distanzia_le_letture()
    {
        var before = ScanPace.Network;
        try
        {
            ScanPace.Network = ScanSpeed.Leggera; // 25 letture al secondo
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 11; i++)
                ScanPace.Wait(network: true);
            Assert.True(watch.ElapsedMilliseconds >= 350, $"Durata: {watch.ElapsedMilliseconds} ms");
            Assert.Equal(1, ScanPace.Parallelism(network: true, normal: 4));

            ScanPace.Network = ScanSpeed.Massima;
            watch.Restart();
            for (var i = 0; i < 1000; i++)
                ScanPace.Wait(network: true);
            Assert.True(watch.ElapsedMilliseconds < 200);
        }
        finally
        {
            ScanPace.Network = before;
        }
    }

    [Fact]
    public void In_background_restituisce_il_risultato() => Assert.Equal(42, ScanPace.InBackground(() => 42));
}

public class DiskTests
{
    [Theory]
    [InlineData(@"C:\Users\Mario\Download\a.exe", @"C:\")]
    [InlineData(@"e:\foto", @"E:\")]
    [InlineData(@"\\NAS\Foto\2024\x.jpg", @"\\NAS\Foto")]
    public void Riconosce_il_disco_di_un_percorso(string path, string volume) =>
        Assert.Equal(volume, Drives.DiskActivity.VolumeOf(path));

    [Fact]
    public void Conta_le_letture_per_disco()
    {
        Drives.DiskActivity.RecordDirectory(@"\\TEST-DISCO\Dati\a", TimeSpan.FromMilliseconds(10));
        Drives.DiskActivity.RecordFile(@"\\TEST-DISCO\Dati\a\b.pdf", TimeSpan.FromMilliseconds(30));
        var stats = Drives.DiskActivity.Get(@"\\TEST-DISCO\Dati");
        Assert.Equal(1, stats.DirectoriesRead);
        Assert.Equal(1, stats.FilesRead);
        Assert.Equal(20, stats.AverageTime.TotalMilliseconds, 3);
        Assert.NotNull(stats.LastAccessUtc);
    }

    [Fact]
    public void Segnala_i_dischi_collegati_e_scollegati()
    {
        var c = new Drives.DriveSnapshot(@"C:\", "Sistema", DriveType.Fixed, "NTFS", 100, 50, "AAAA");
        var e = new Drives.DriveSnapshot(@"E:\", "CHIAVETTA", DriveType.Removable, "exFAT", 32, 30, "BBBB");
        var current = new List<Drives.DriveSnapshot> { c };
        using var watcher = new Drives.DriveWatcher(TimeSpan.FromHours(1), () => current);
        var arrived = new List<string>();
        var removed = new List<string>();
        watcher.Arrived += d => arrived.Add(d.Name);
        watcher.Removed += d => removed.Add(d.Name);

        current = [c, e];
        watcher.Poll();
        Assert.Equal([@"E:\"], arrived);
        current = [c];
        watcher.Poll();
        Assert.Equal([@"E:\"], removed);
    }
}
