using System.Buffers.Binary;
using System.Text;

namespace PcOrg.Core.Content;

/// <summary>
/// Elenca i file contenuti in un'immagine ISO (CD/DVD) senza montarla.
/// </summary>
/// <remarks>
/// Legge il file system ISO 9660: i descrittori di volume partono dal settore 16 (settori da 2048 byte).
/// Se è presente l'estensione Joliet (nomi lunghi in Unicode) si usa quella, altrimenti il volume primario.
/// Ogni cartella è un elenco di record: lunghezza, posizione e dimensione dei dati, attributi, nome.
/// Le ISO con solo file system UDF (alcune immagini di Windows) espongono qui solo pochi file di cortesia.
/// </remarks>
public static class IsoReader
{
    private const int Sector = 2048;
    private const int MaxEntries = 5000;
    private const int MaxDirectoryBytes = 4 * 1024 * 1024;

    public static IReadOnlyList<string> ListFiles(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ListFiles(stream);
    }

    public static IReadOnlyList<string> ListFiles(Stream stream)
    {
        (uint Lba, uint Size)? primary = null, joliet = null;
        var buffer = new byte[Sector];
        for (var sector = 16; sector < 64; sector++)
        {
            if (!ReadAt(stream, (long)sector * Sector, buffer) || Encoding.ASCII.GetString(buffer, 1, 5) != "CD001")
                break;
            var type = buffer[0];
            if (type == 255)
                break; // terminatore dei descrittori
            var root = (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(156 + 2)), BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(156 + 10)));
            if (type == 1)
                primary = root;
            else if (type == 2 && buffer[88] == 0x25 && buffer[89] == 0x2F && buffer[90] is 0x40 or 0x43 or 0x45)
                joliet = root; // sequenze di escape che indicano Joliet (UCS-2)
        }

        var files = new List<string>();
        if ((joliet ?? primary) is { } start)
            Walk(stream, start.Lba, start.Size, "", joliet is not null, files, depth: 0);
        return files;
    }

    private static void Walk(Stream stream, uint lba, uint size, string prefix, bool unicode, List<string> files, int depth)
    {
        if (depth > 16 || size == 0 || size > MaxDirectoryBytes || files.Count >= MaxEntries)
            return;
        var data = new byte[size];
        if (!ReadAt(stream, (long)lba * Sector, data))
            return;

        var pos = 0;
        while (pos < data.Length && files.Count < MaxEntries)
        {
            int length = data[pos];
            if (length == 0)
            {
                pos = (pos / Sector + 1) * Sector; // i record non attraversano i settori: si passa al successivo
                continue;
            }
            if (pos + 33 > data.Length || pos + length > data.Length)
                break;
            var extent = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 2));
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 10));
            var isDirectory = (data[pos + 25] & 0x02) != 0;
            int nameLength = data[pos + 32];
            if (pos + 33 + nameLength <= data.Length && !(nameLength == 1 && data[pos + 33] <= 1)) // esclude "." e ".."
            {
                var name = (unicode ? Encoding.BigEndianUnicode : Encoding.ASCII).GetString(data, pos + 33, nameLength);
                var version = name.LastIndexOf(';');
                if (version > 0)
                    name = name[..version]; // toglie il numero di versione ";1"
                name = name.TrimEnd('.');
                if (isDirectory)
                    Walk(stream, extent, dataSize, prefix + name + "/", unicode, files, depth + 1);
                else if (name.Length > 0)
                    files.Add(prefix + name);
            }
            pos += length;
        }
    }

    private static bool ReadAt(Stream stream, long offset, byte[] buffer)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
            return false;
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return true;
    }
}
