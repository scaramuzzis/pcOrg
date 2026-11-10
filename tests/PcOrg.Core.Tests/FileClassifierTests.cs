using PcOrg.Core.Classification;

namespace PcOrg.Core.Tests;

/// <summary>Classificazione dei file per categoria.</summary>
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
    [InlineData(@"D:\Backup\pc-ufficio.GHO", FileCategory.ImmagineDisco)]
    [InlineData(@"D:\Backup\pc-ufficio001.ghs", FileCategory.ImmagineDisco)]
    [InlineData(@"D:\Backup\sistema.tibx", FileCategory.ImmagineDisco)]
    [InlineData(@"D:\Backup\sistema.mrimg", FileCategory.ImmagineDisco)]
    [InlineData(@"D:\VM\disco.vdi", FileCategory.ImmagineDisco)]
    [InlineData(@"C:\Download\manuale.PDF", FileCategory.Pdf)]
    [InlineData(@"C:\Download\lettera.docx", FileCategory.Word)]
    [InlineData(@"C:\Download\vecchio.doc", FileCategory.Word)]
    [InlineData(@"C:\Download\leggimi.txt", FileCategory.Testo)]
    [InlineData(@"C:\Download\bilancio.xlsx", FileCategory.FoglioDiCalcolo)]
    [InlineData(@"C:\Download\slide.pptx", FileCategory.Presentazione)]
    [InlineData(@"C:\Download\libro.epub", FileCategory.Documento)]
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

    [Theory]
    [InlineData(".gho", "Ghost (.gho, .ghs)")]
    [InlineData(".GHS", "Ghost (.gho, .ghs)")]
    [InlineData(".iso", "ISO (CD/DVD)")]
    [InlineData(".vhdx", "Dischi virtuali (VHD, VMDK...)")]
    [InlineData(".pdf", null)]
    public void Sottocategorie_delle_immagini_disco(string ext, string? expected)
    {
        Assert.Equal(expected, Subcategories.Find(ext)?.Name);
    }

    [Fact]
    public void Ogni_estensione_ha_una_sola_sottocategoria()
    {
        var all = Subcategories.All.SelectMany(s => s.Extensions).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
