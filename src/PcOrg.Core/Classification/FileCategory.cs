namespace PcOrg.Core.Classification;

/// <summary>Categorie in cui vengono organizzati i file indicizzati.</summary>
/// <remarks>I valori numerici sono salvati nel database: non riordinarli, aggiungere solo in coda.</remarks>
public enum FileCategory
{
    Altro = 0,
    Installer = 1,
    Driver = 2,
    Programma = 3,
    Archivio = 4,
    ImmagineDisco = 5,
    Documento = 6,
    Immagine = 7,
    Audio = 8,
    Video = 9,
    Script = 10,
    Pdf = 11,
    Word = 12,
    Testo = 13,
    FoglioDiCalcolo = 14,
    Presentazione = 15,
}

/// <summary>Testi da mostrare per le categorie.</summary>
public static class FileCategoryExtensions
{
    /// <summary>Nome da mostrare nell'interfaccia.</summary>
    public static string DisplayName(this FileCategory category) => category switch
    {
        FileCategory.Installer => "Installer",
        FileCategory.Driver => "Driver",
        FileCategory.Programma => "Programmi",
        FileCategory.Archivio => "Archivi",
        FileCategory.ImmagineDisco => "Immagini disco",
        FileCategory.Documento => "Altri documenti",
        FileCategory.Immagine => "Immagini",
        FileCategory.Audio => "Audio",
        FileCategory.Video => "Video",
        FileCategory.Script => "Script",
        FileCategory.Pdf => "PDF",
        FileCategory.Word => "Word",
        FileCategory.Testo => "Testo",
        FileCategory.FoglioDiCalcolo => "Fogli di calcolo",
        FileCategory.Presentazione => "Presentazioni",
        _ => "Altro",
    };
}
