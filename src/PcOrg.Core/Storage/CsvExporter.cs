using System.Globalization;
using System.Text;

namespace PcOrg.Core.Storage;

/// <summary>
/// Esporta un elenco di file in CSV pensato per Excel in italiano: separatore ";",
/// codifica UTF-8 con BOM (così le lettere accentate si vedono correttamente).
/// </summary>
public static class CsvExporter
{
    private static readonly string[] Header =
        ["Nome", "Categoria", "Versione", "Produttore", "Prodotto", "Firma", "Architettura",
         "Dimensione (byte)", "Modificato", "Cartella", "Percorso"];

    public static void Write(IEnumerable<FileRecord> files, string path)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Write(files, writer);
    }

    public static void Write(IEnumerable<FileRecord> files, TextWriter writer)
    {
        writer.WriteLine(string.Join(';', Header));
        var italian = CultureInfo.GetCultureInfo("it-IT");
        foreach (var f in files)
        {
            string[] row =
            [
                f.Name, f.CategoryName, f.Version ?? "", f.Company ?? "", f.ProductName ?? "",
                f.Meta?.Signer ?? "", f.ArchitectureText ?? "", f.Size.ToString(CultureInfo.InvariantCulture),
                f.ModifiedUtc.ToLocalTime().ToString("g", italian), f.Folder, f.Path,
            ];
            writer.WriteLine(string.Join(';', row.Select(Escape)));
        }
    }

    /// <summary>Racchiude tra virgolette i valori con ; " o a capo, raddoppiando le virgolette interne.</summary>
    internal static string Escape(string value) =>
        value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
}
