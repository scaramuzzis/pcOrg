namespace PcOrg.Core.Classification;

/// <summary>Suddivisione di una categoria per tipo di file (es. Immagini disco → Ghost, ISO, dischi virtuali...).</summary>
public sealed record Subcategory(FileCategory Category, string Name, IReadOnlyList<string> Extensions);

public static class Subcategories
{
    public static IReadOnlyList<Subcategory> All { get; } =
    [
        new(FileCategory.ImmagineDisco, "Ghost (.gho, .ghs)", [".gho", ".ghs"]),
        new(FileCategory.ImmagineDisco, "ISO (CD/DVD)", [".iso"]),
        new(FileCategory.ImmagineDisco, "Altri formati CD/DVD", [".nrg", ".mdf", ".mds", ".cue", ".ccd", ".cdi", ".dmg"]),
        new(FileCategory.ImmagineDisco, "Dischi virtuali (VHD, VMDK...)", [".vhd", ".vhdx", ".vmdk", ".vdi", ".qcow2"]),
        new(FileCategory.ImmagineDisco, "Immagini Windows (WIM, ESD)", [".wim", ".esd", ".swm"]),
        new(FileCategory.ImmagineDisco, "Backup Acronis", [".tib", ".tibx"]),
        new(FileCategory.ImmagineDisco, "Backup Macrium", [".mrimg", ".mrbak"]),
        new(FileCategory.ImmagineDisco, "Immagini raw (IMG, DD)", [".img", ".dd"]),
    ];

    public static IEnumerable<Subcategory> For(FileCategory category) => All.Where(s => s.Category == category);

    /// <summary>Sottocategoria di un'estensione (es. ".gho" → Ghost), se esiste.</summary>
    public static Subcategory? Find(string extension) =>
        All.FirstOrDefault(s => s.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
}
