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
