using PcOrg.Core.Classification;

namespace PcOrg.Core.Tests;

public class FileClassifierTests
{
    [Theory]
    [InlineData(@"C:\Download\7z2408-x64.msi", FileCategory.Installer)]
    [InlineData(@"C:\Download\ChromeSetup.exe", FileCategory.Installer)]
    [InlineData(@"C:\Download\vlc-3.0.21-win64-installer.exe", FileCategory.Installer)]
    [InlineData(@"C:\Download\nvidia_driver_566.36.exe", FileCategory.Driver)]
    [InlineData(@"C:\Download\Drivers\Realtek\audio.exe", FileCategory.Driver)]
    [InlineData(@"C:\Download\Drivers\Realtek\RtHDA.inf", FileCategory.Driver)]
    [InlineData(@"C:\Download\intel-driver-pack.zip", FileCategory.Driver)]
    [InlineData(@"C:\Download\putty.exe", FileCategory.Programma)]
    [InlineData(@"C:\Download\backup.7z", FileCategory.Archivio)]
    [InlineData(@"C:\Download\Win11_24H2_Italian_x64.iso", FileCategory.ImmagineDisco)]
    [InlineData(@"C:\Download\manuale.PDF", FileCategory.Documento)]
    [InlineData(@"C:\Download\foto.jpg", FileCategory.Immagine)]
    [InlineData(@"C:\Download\pulizia.ps1", FileCategory.Script)]
    [InlineData(@"C:\Download\senza-estensione", FileCategory.Altro)]
    public void Classifica_per_estensione_nome_e_cartella(string path, FileCategory expected)
    {
        Assert.Equal(expected, FileClassifier.Classify(path.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void Un_nome_che_contiene_la_parola_solo_a_meta_non_e_un_driver()
    {
        // "hydrvault" non inizia con "drv" né con "driver"
        Assert.Equal(FileCategory.Programma, FileClassifier.Classify("hydrvault.exe"));
    }

    [Fact]
    public void Tokenize_divide_su_separatori_e_camel_case()
    {
        Assert.Equal(["Chrome", "Setup", "x"], FileClassifier.Tokenize("ChromeSetup_x64"));
    }
}
