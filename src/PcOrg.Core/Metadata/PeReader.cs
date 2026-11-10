using System.Buffers.Binary;
using System.Text;

namespace PcOrg.Core.Metadata;

/// <summary>Dati grezzi letti da un file eseguibile Windows (formato PE).</summary>
public sealed class PeInfo
{
    public Architecture Architecture { get; init; }
    public IReadOnlyDictionary<string, string> VersionStrings { get; init; } = new Dictionary<string, string>();
    public string? FixedFileVersion { get; init; }
    public string? FixedProductVersion { get; init; }
    /// <summary>Firma Authenticode (PKCS#7) incorporata nel file, se presente.</summary>
    public byte[]? Certificate { get; init; }
    public IReadOnlyList<string> SectionNames { get; init; } = [];
    /// <summary>Posizione dei dati accodati dopo l'ultima sezione (dove gli installer mettono i file compressi).</summary>
    public long OverlayOffset { get; init; }

    public string? Get(string key) => VersionStrings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}

/// <summary>
/// Lettore minimale del formato PE: architettura, risorsa VERSIONINFO, firma, sezioni.
/// Non usa API di Windows, quindi funziona (ed è testabile) su qualsiasi sistema.
/// </summary>
public static class PeReader
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxResourceBytes = 1024 * 1024;
    private const int MaxCertificateBytes = 1024 * 1024;

    private readonly record struct Section(string Name, uint VirtualAddress, uint VirtualSize, uint RawPointer, uint RawSize);

    /// <summary>Legge il file; restituisce null se non è un PE valido.</summary>
    public static PeInfo? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1, FileOptions.RandomAccess);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static PeInfo? Read(Stream stream)
    {
        var header = ReadAt(stream, 0, (int)Math.Min(MaxHeaderBytes, stream.Length));
        if (header.Length < 0x40 || header[0] != 'M' || header[1] != 'Z')
            return null;

        var peOffset = (int)U32(header, 0x3C);
        if (peOffset <= 0 || peOffset + 24 > header.Length || U32(header, peOffset) != 0x00004550) // "PE\0\0"
            return null;

        var coff = peOffset + 4;
        var machine = U16(header, coff);
        var sectionCount = U16(header, coff + 2);
        var optionalSize = U16(header, coff + 16);
        var optional = coff + 20;
        if (optional + optionalSize > header.Length || optionalSize < 2)
            return null;

        var magic = U16(header, optional);
        int dirCountOffset, dirOffset;
        switch (magic)
        {
            case 0x10B: dirCountOffset = optional + 92; dirOffset = optional + 96; break;   // PE32
            case 0x20B: dirCountOffset = optional + 108; dirOffset = optional + 112; break; // PE32+
            default: return null;
        }
        var dirCount = dirCountOffset + 4 <= header.Length ? U32(header, dirCountOffset) : 0;

        (uint Address, uint Size) Directory(int index) =>
            index < dirCount && dirOffset + index * 8 + 8 <= optional + optionalSize
                ? (U32(header, dirOffset + index * 8), U32(header, dirOffset + index * 8 + 4))
                : (0, 0);

        var sections = new List<Section>();
        var sectionTable = optional + optionalSize;
        for (var i = 0; i < sectionCount && sectionTable + (i + 1) * 40 <= header.Length; i++)
        {
            var s = sectionTable + i * 40;
            var name = Encoding.ASCII.GetString(header, s, 8).TrimEnd('\0');
            sections.Add(new Section(name, U32(header, s + 12), U32(header, s + 8), U32(header, s + 20), U32(header, s + 16)));
        }

        long? ToOffset(uint rva)
        {
            foreach (var s in sections)
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize))
                    return s.RawPointer + (rva - s.VirtualAddress);
            return null;
        }

        var architecture = MachineToArchitecture(machine);
        var clr = Directory(14);
        if (clr.Address != 0 && ToOffset(clr.Address) is { } clrOffset)
        {
            var cor = ReadAt(stream, clrOffset, 72);
            if (cor.Length >= 20)
            {
                var flags = U32(cor, 16);
                const uint IlOnly = 0x1, Requires32Bit = 0x2, Prefers32Bit = 0x20000;
                if ((flags & IlOnly) != 0 && machine == 0x14C && (flags & Requires32Bit) == 0)
                    architecture = Architecture.AnyCpu;
                else if ((flags & IlOnly) != 0 && machine == 0x14C && (flags & Prefers32Bit) != 0)
                    architecture = Architecture.X86;
            }
        }

        var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? fixedFile = null, fixedProduct = null;
        var resources = Directory(2);
        if (resources.Address != 0 && ToOffset(resources.Address) is { } resOffset)
        {
            var section = sections.First(s => resources.Address >= s.VirtualAddress
                && resources.Address < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize));
            var available = (int)Math.Min(MaxResourceBytes, section.RawPointer + (long)section.RawSize - resOffset);
            var res = ReadAt(stream, resOffset, Math.Max(0, available));
            if (FindVersionResource(res, resources.Address) is { } leaf && ToOffset(leaf.Rva) is { } dataOffset)
            {
                var data = ReadAt(stream, dataOffset, (int)Math.Min(leaf.Size, MaxResourceBytes));
                ParseVersionInfo(data, info, out fixedFile, out fixedProduct);
            }
        }

        // Directory 4 (sicurezza): contiene un offset nel file, non un RVA.
        byte[]? certificate = null;
        var security = Directory(4);
        if (security.Address != 0 && security.Size > 8 && security.Size <= MaxCertificateBytes
            && security.Address + (long)security.Size <= stream.Length)
        {
            var win = ReadAt(stream, security.Address, (int)security.Size);
            var length = (int)Math.Min(U32(win, 0), (uint)win.Length);
            if (U16(win, 6) == 0x0002 && length > 8) // WIN_CERT_TYPE_PKCS_SIGNED_DATA
                certificate = TrimDer(win.AsSpan(8, length - 8));
        }

        var overlay = sections.Count == 0 ? 0 : sections.Max(s => (long)s.RawPointer + s.RawSize);

        return new PeInfo
        {
            Architecture = architecture,
            VersionStrings = info,
            FixedFileVersion = fixedFile,
            FixedProductVersion = fixedProduct,
            Certificate = certificate,
            SectionNames = sections.Select(s => s.Name).ToList(),
            OverlayOffset = overlay,
        };
    }

    private static Architecture MachineToArchitecture(ushort machine) => machine switch
    {
        0x14C => Architecture.X86,
        0x8664 => Architecture.X64,
        0xAA64 => Architecture.Arm64,
        0x1C0 or 0x1C4 => Architecture.Arm,
        _ => Architecture.Sconosciuta,
    };

    // ------------------------------------------------------------------ Risorse

    /// <summary>Cerca la risorsa RT_VERSION (tipo 16) nell'albero delle risorse: tipo → nome → lingua.</summary>
    private static (uint Rva, uint Size)? FindVersionResource(byte[] res, uint resourceRva)
    {
        const int RtVersion = 16;
        var nameDir = FindEntry(res, 0, RtVersion);
        if (nameDir is not { IsDirectory: true })
            return null;
        var langDir = FirstEntry(res, nameDir.Value.Offset);
        if (langDir is not { IsDirectory: true })
            return null;
        var leaf = FirstEntry(res, langDir.Value.Offset);
        if (leaf is not { IsDirectory: false } || leaf.Value.Offset + 8 > res.Length)
            return null;
        return (U32(res, leaf.Value.Offset), U32(res, leaf.Value.Offset + 4));
    }

    private readonly record struct ResEntry(uint Id, bool IsNamed, bool IsDirectory, int Offset);

    private static IEnumerable<ResEntry> Entries(byte[] res, int dirOffset)
    {
        if (dirOffset < 0 || dirOffset + 16 > res.Length)
            yield break;
        var count = U16(res, dirOffset + 12) + U16(res, dirOffset + 14);
        for (var i = 0; i < count; i++)
        {
            var e = dirOffset + 16 + i * 8;
            if (e + 8 > res.Length)
                yield break;
            var name = U32(res, e);
            var data = U32(res, e + 4);
            yield return new ResEntry(name & 0x7FFFFFFF, (name & 0x80000000) != 0, (data & 0x80000000) != 0, (int)(data & 0x7FFFFFFF));
        }
    }

    private static ResEntry? FindEntry(byte[] res, int dirOffset, uint id)
    {
        foreach (var e in Entries(res, dirOffset))
            if (!e.IsNamed && e.Id == id)
                return e;
        return null;
    }

    private static ResEntry? FirstEntry(byte[] res, int dirOffset)
    {
        foreach (var e in Entries(res, dirOffset))
            return e;
        return null;
    }

    // ------------------------------------------------------------------ VS_VERSIONINFO

    private readonly record struct Block(int Start, int Length, int ValueLength, int Type, string Key, int ValueOffset, int End);

    /// <summary>Legge l'intestazione di un blocco (wLength, wValueLength, wType, szKey) allineato a 32 bit.</summary>
    private static Block? ReadBlock(byte[] data, int start, int limit)
    {
        if (start + 6 > limit)
            return null;
        int length = U16(data, start);
        if (length < 6 || start + length > limit)
            return null;
        var keyStart = start + 6;
        var keyEnd = keyStart;
        while (keyEnd + 1 < start + length && (data[keyEnd] != 0 || data[keyEnd + 1] != 0))
            keyEnd += 2;
        var key = Encoding.Unicode.GetString(data, keyStart, keyEnd - keyStart);
        var valueOffset = Align4(keyEnd + 2);
        return new Block(start, length, U16(data, start + 2), U16(data, start + 4), key, valueOffset, start + length);
    }

    private static IEnumerable<Block> Children(byte[] data, int from, int end)
    {
        var pos = Align4(from);
        while (pos < end && ReadBlock(data, pos, end) is { } child)
        {
            yield return child;
            pos = Align4(child.End);
        }
    }

    private static void ParseVersionInfo(byte[] data, Dictionary<string, string> strings, out string? fileVersion, out string? productVersion)
    {
        fileVersion = productVersion = null;
        if (ReadBlock(data, 0, data.Length) is not { Key: "VS_VERSION_INFO" } root)
            return;

        var childrenStart = root.ValueOffset;
        if (root.ValueLength >= 52 && root.ValueOffset + 52 <= data.Length && U32(data, root.ValueOffset) == 0xFEEF04BD)
        {
            fileVersion = FormatVersion(U32(data, root.ValueOffset + 8), U32(data, root.ValueOffset + 12));
            productVersion = FormatVersion(U32(data, root.ValueOffset + 16), U32(data, root.ValueOffset + 20));
            childrenStart = root.ValueOffset + root.ValueLength;
        }

        foreach (var child in Children(data, childrenStart, root.End))
        {
            if (child.Key != "StringFileInfo")
                continue;
            // Si usa la prima tabella di stringhe (di solito l'unica lingua presente).
            foreach (var table in Children(data, child.ValueOffset, child.End))
            {
                foreach (var entry in Children(data, table.ValueOffset, table.End))
                {
                    // wValueLength è in caratteri; si limita comunque alla fine del blocco.
                    var bytes = Math.Min(entry.ValueLength * 2, entry.End - entry.ValueOffset);
                    if (bytes <= 0)
                        continue;
                    var value = Encoding.Unicode.GetString(data, entry.ValueOffset, bytes).TrimEnd('\0').Trim();
                    if (value.Length > 0)
                        strings.TryAdd(entry.Key, value);
                }
                if (strings.Count > 0)
                    return;
            }
        }
    }

    private static string? FormatVersion(uint ms, uint ls) =>
        ms == 0 && ls == 0 ? null : $"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}";

    // ------------------------------------------------------------------ Helper

    /// <summary>La firma PKCS#7 può avere byte di riempimento finali: si tiene solo la struttura DER.</summary>
    private static byte[] TrimDer(ReadOnlySpan<byte> der)
    {
        if (der.Length > 4 && der[0] == 0x30)
        {
            int len = der[1];
            var header = 2;
            if ((len & 0x80) != 0)
            {
                var n = len & 0x7F;
                if (n is >= 1 and <= 4 && der.Length > 2 + n)
                {
                    len = 0;
                    for (var i = 0; i < n; i++)
                        len = (len << 8) | der[2 + i];
                    header = 2 + n;
                }
            }
            if (len > 0 && header + len <= der.Length)
                return der[..(header + len)].ToArray();
        }
        return der.ToArray();
    }

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        if (offset < 0 || offset >= stream.Length || count <= 0)
            return [];
        count = (int)Math.Min(count, stream.Length - offset);
        var buffer = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static int Align4(int value) => (value + 3) & ~3;
    private static ushort U16(byte[] b, int o) => o + 2 <= b.Length ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)) : (ushort)0;
    private static uint U32(byte[] b, int o) => o + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)) : 0;

    /// <summary>Cerca una sequenza di byte in una porzione del file (usato per riconoscere gli installer).</summary>
    internal static bool Contains(Stream stream, long offset, int length, ReadOnlySpan<byte> pattern)
    {
        var data = ReadAt(stream, offset, length);
        return data.AsSpan().IndexOf(pattern) >= 0;
    }
}
