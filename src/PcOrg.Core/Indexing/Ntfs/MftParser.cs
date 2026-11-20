using System.Buffers.Binary;
using System.Text;

namespace PcOrg.Core.Indexing.Ntfs;

/// <summary>Un nome di un record MFT (un file con più collegamenti fisici ne ha più d'uno).</summary>
/// <param name="Namespace">0 = POSIX, 1 = Win32, 2 = DOS (nome corto 8.3), 3 = Win32 e DOS.</param>
public readonly record struct MftName(ulong ParentFrn, string Name, byte Namespace);

/// <summary>Contenuto utile di un record della Master File Table.</summary>
public sealed class MftRecord
{
    public ulong Number { get; init; }
    /// <summary>Record base, se questo è un record di estensione (0 = è un record base).</summary>
    public ulong BaseRecord { get; init; }
    public bool InUse { get; init; }
    public bool IsDirectory { get; init; }
    public List<MftName> Names { get; } = [];
    public long? ModifiedFileTime { get; set; }
    public FileAttributes? Attributes { get; set; }
    /// <summary>Dimensione del flusso dati principale (senza nome).</summary>
    public long? DataSize { get; set; }
    /// <summary>Tag del reparse point (collegamenti a cartelle, file di OneDrive…), se presente.</summary>
    public uint? ReparseTag { get; set; }
    /// <summary>Estensioni (LCN, numero di cluster) del flusso dati principale: servono per il record 0 ($MFT).</summary>
    public List<(long Lcn, long Clusters)> DataRuns { get; } = [];
}

/// <summary>
/// Interpreta i record della Master File Table di NTFS (formato documentato da Microsoft): numeri di
/// sequenza di aggiornamento (fixup), intestazione e attributi $STANDARD_INFORMATION, $FILE_NAME, $DATA
/// e $REPARSE_POINT. Non usa API di sistema: lavora sui byte, così è verificabile con record costruiti nei test.
/// </summary>
public static class MftParser
{
    private const uint AttrStandardInformation = 0x10, AttrFileName = 0x30, AttrData = 0x80, AttrReparsePoint = 0xC0, AttrEnd = 0xFFFFFFFF;
    private const ulong FrnMask = 0x0000FFFFFFFFFFFF;

    /// <summary>
    /// Legge un record (modificato sul posto dai fixup). Restituisce null se non è un record valido
    /// ("FILE" mancante o fixup incoerenti: record danneggiato o mai usato).
    /// </summary>
    /// <param name="readRuns">Legge anche le estensioni del flusso dati: servono solo per il record 0 ($MFT);
    /// leggendo un volume intero conviene disattivarlo per gli altri record e risparmiare memoria.</param>
    public static MftRecord? Parse(Span<byte> record, int bytesPerSector, ulong number, bool readRuns = true)
    {
        if (record.Length < 48 || record[0] != (byte)'F' || record[1] != (byte)'I' || record[2] != (byte)'L' || record[3] != (byte)'E')
            return null;
        if (!ApplyFixups(record, bytesPerSector))
            return null;

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
        var result = new MftRecord
        {
            Number = number,
            BaseRecord = BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]) & FrnMask,
            InUse = (flags & 0x01) != 0,
            IsDirectory = (flags & 0x02) != 0,
        };
        if (!result.InUse)
            return result;

        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        var used = Math.Min((int)BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]), record.Length);
        while (offset + 16 <= used)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == AttrEnd)
                break;
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (length < 16 || offset + length > used)
                break; // attributo malformato: ci si ferma
            var attribute = record.Slice(offset, length);
            var nonResident = attribute[8] != 0;
            var nameLength = attribute[9];

            if (!nonResident && Resident(attribute) is { Length: > 0 } value)
            {
                switch (type)
                {
                    case AttrStandardInformation when value.Length >= 36:
                        result.ModifiedFileTime = BinaryPrimitives.ReadInt64LittleEndian(value[8..]);
                        result.Attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(value[32..]);
                        break;
                    case AttrFileName when value.Length >= 66:
                        var chars = value[64];
                        if (value.Length >= 66 + chars * 2)
                            result.Names.Add(new MftName(BinaryPrimitives.ReadUInt64LittleEndian(value) & FrnMask,
                                Encoding.Unicode.GetString(value.Slice(66, chars * 2)), value[65]));
                        break;
                    case AttrData when nameLength == 0:
                        result.DataSize = value.Length;
                        break;
                    case AttrReparsePoint when value.Length >= 4:
                        result.ReparseTag = BinaryPrimitives.ReadUInt32LittleEndian(value);
                        break;
                }
            }
            else if (nonResident && length >= 0x40)
            {
                if (type == AttrData && nameLength == 0)
                {
                    var startVcn = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x10..]);
                    if (startVcn == 0)
                        result.DataSize = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]);
                    int runs = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x20..]);
                    if (readRuns && runs < length)
                        ParseRuns(attribute[runs..], result.DataRuns);
                }
                else if (type == AttrReparsePoint)
                {
                    result.ReparseTag ??= 0; // contenuto fuori dal record: tag non leggibile qui
                }
            }
            offset += length;
        }
        return result;
    }

    /// <summary>Valore di un attributo residente (contenuto dentro il record).</summary>
    private static Span<byte> Resident(Span<byte> attribute)
    {
        if (attribute.Length < 0x18)
            return [];
        var valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..]);
        int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..]);
        return valueOffset + valueLength <= attribute.Length ? attribute.Slice(valueOffset, valueLength) : [];
    }

    /// <summary>
    /// Ripristina gli ultimi due byte di ogni settore, che sul disco contengono il numero di sequenza
    /// di aggiornamento: se un settore non corrisponde il record è stato scritto a metà e va scartato.
    /// </summary>
    public static bool ApplyFixups(Span<byte> record, int bytesPerSector)
    {
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaCount < 1 || usaOffset + usaCount * 2 > record.Length || (usaCount - 1) * bytesPerSector > record.Length)
            return false;
        var usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (var i = 1; i < usaCount; i++)
        {
            var end = i * bytesPerSector - 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[end..]) != usn)
                return false;
            record[end] = record[usaOffset + i * 2];
            record[end + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    /// <summary>
    /// Elenco delle estensioni ("data run"): per ognuna, byte di intestazione (dimensioni dei due campi),
    /// numero di cluster e spostamento dell'LCN rispetto all'estensione precedente (con segno).
    /// </summary>
    public static void ParseRuns(ReadOnlySpan<byte> runs, List<(long Lcn, long Clusters)> result)
    {
        long lcn = 0;
        var i = 0;
        while (i < runs.Length && runs[i] != 0)
        {
            var header = runs[i++];
            int lengthSize = header & 0x0F, offsetSize = header >> 4;
            if (lengthSize == 0 || i + lengthSize + offsetSize > runs.Length)
                break;
            long clusters = 0;
            for (var b = 0; b < lengthSize; b++)
                clusters |= (long)runs[i + b] << (8 * b);
            i += lengthSize;
            if (offsetSize == 0)
                continue; // estensione "sparsa": niente cluster sul disco
            long delta = 0;
            for (var b = 0; b < offsetSize; b++)
                delta |= (long)runs[i + b] << (8 * b);
            if (offsetSize < 8 && (runs[i + offsetSize - 1] & 0x80) != 0)
                delta -= 1L << (8 * offsetSize); // estensione del segno
            i += offsetSize;
            lcn += delta;
            result.Add((lcn, clusters));
        }
    }
}
