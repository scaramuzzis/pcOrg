using System.Text;
using PcOrg.Core.Drivers;
using PcOrg.Core.Indexing;
using PcOrg.Core.Metadata;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Tests;

/// <summary>Pacchetti driver (.inf) e confronto con le periferiche presenti.</summary>
public class DriverTests
{
    internal const string RealtekInf = """
        ; Driver audio di esempio
        [Version]
        Signature   = "$Windows NT$"
        Class       = MEDIA
        ClassGuid   = {4d36e96c-e325-11ce-bfc1-08002be10318}
        Provider    = %ProviderName%
        DriverVer   = 07/18/2024,6.0.9777.1
        CatalogFile = RtHDA.cat

        [Manufacturer]
        %MfgName% = Realtek, NTamd64, NTamd64.10.0...22000

        [Realtek.NTamd64]
        %HdAudioDesc% = RtHDA_Install, HDAUDIO\FUNC_01&VEN_10EC&DEV_0255, HDAUDIO\FUNC_01&VEN_10EC&DEV_0256 ; due codec

        [Realtek.NTamd64.10.0...22000]
        %HdAudioDesc% = RtHDA_Install, HDAUDIO\FUNC_01&VEN_10EC&DEV_0257, \
                        HDAUDIO\FUNC_01&VEN_10EC&DEV_0236

        [Strings]
        ProviderName = "Realtek Semiconductor Corp."
        MfgName      = "Realtek"
        HdAudioDesc  = "Realtek High Definition Audio"
        """;

    [Fact]
    public void Legge_classe_fornitore_versione_e_identificativi()
    {
        var inf = InfReader.Parse(RealtekInf)!;

        Assert.Equal("MEDIA", inf.Class);
        Assert.Equal("Realtek Semiconductor Corp.", inf.Provider);
        Assert.Equal("6.0.9777.1", inf.Version);
        Assert.Equal(new DateTime(2024, 7, 18), inf.Date);
        Assert.Equal(4, inf.HardwareIds.Count);
        Assert.Contains(inf.HardwareIds, i => i.Id == @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0236"); // riga spezzata con "\"
        Assert.All(inf.HardwareIds, i => Assert.Equal("Realtek High Definition Audio", i.Description));
    }

    [Fact]
    public void Legge_i_file_inf_in_utf16()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "RtHDA.inf");
        File.WriteAllText(path, RealtekInf, Encoding.Unicode); // con BOM UTF-16 LE, come molti driver reali
        var meta = MetadataExtractor.Extract(path)!;

        Assert.Equal("Realtek Semiconductor Corp.", meta.CompanyName);
        Assert.Equal("Realtek High Definition Audio", meta.ProductName);
        Assert.Equal("6.0.9777.1", meta.ProductVersion);
        Assert.Equal(4, meta.HardwareIds!.Count);
    }

    private static Device Codec(string dev, string? version) => new("Realtek High Definition Audio", "MEDIA",
        [$@"HDAUDIO\FUNC_01&VEN_10EC&DEV_{dev}&SUBSYS_1028087D&REV_1000", $@"HDAUDIO\FUNC_01&VEN_10EC&DEV_{dev}&SUBSYS_1028087D"],
        [$@"HDAUDIO\FUNC_01&VEN_10EC&DEV_{dev}", @"HDAUDIO\FUNC_01&VEN_10EC"], version, "Realtek");

    [Theory]
    [InlineData("0255", "6.0.9600.1", DriverState.PiuRecente)]
    [InlineData("0255", "6.0.9777.1", DriverState.StessaVersione)]
    [InlineData("0256", "6.0.9800.1", DriverState.Superato)]
    [InlineData("0256", null, DriverState.Compatibile)]
    [InlineData("0299", "6.0.9600.1", DriverState.NonServe)]
    public void Confronta_con_le_periferiche_presenti(string dev, string? installed, DriverState expected)
    {
        var inf = InfReader.Parse(RealtekInf)!;
        var matcher = new DriverMatcher([Codec(dev, installed), new Device("Tastiera", "Keyboard", [@"HID\VID_046D"], [], "1.0", "Microsoft")]);
        var match = matcher.Match(inf.HardwareIds, inf.Version);
        Assert.Equal(expected, match.State);
        if (expected == DriverState.NonServe)
            Assert.Equal("Non serve a questo PC", match.Text);
        else
            Assert.StartsWith("Per Realtek High Definition Audio", match.Text);
    }

    [Fact]
    public void L_indice_conserva_gli_identificativi_dei_driver()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Dir("Driver"), "RtHDA.inf");
        File.WriteAllText(path, RealtekInf, Encoding.Unicode);
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var root = index.AddRoot(System.IO.Path.GetDirectoryName(path)!);
        index.UpdateRoot(root.Id, root.Path);
        MetadataUpdater.Run(index);

        var (file, ids) = Assert.Single(index.GetDriverPackages());
        Assert.Equal("RtHDA.inf", file.Name);
        Assert.Equal(4, ids.Count);
        Assert.Equal("6.0.9777.1", file.Version);

        File.Delete(path);
        index.UpdateRoot(root.Id, root.Path, full: true);
        Assert.Empty(index.GetDriverPackages()); // eliminato il file, spariscono anche i suoi identificativi
    }

    [Fact]
    public void La_lettura_delle_periferiche_non_fallisce()
    {
        var devices = DeviceInventory.Read(); // su Windows legge davvero, altrove è vuoto
        if (OperatingSystem.IsWindows())
            Assert.NotEmpty(devices);
        else
            Assert.Empty(devices);
    }
}
