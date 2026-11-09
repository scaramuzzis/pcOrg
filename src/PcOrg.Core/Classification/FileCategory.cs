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
}

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
        FileCategory.Documento => "Documenti",
        FileCategory.Immagine => "Immagini",
        FileCategory.Audio => "Audio",
        FileCategory.Video => "Video",
        FileCategory.Script => "Script",
        _ => "Altro",
    };
}
