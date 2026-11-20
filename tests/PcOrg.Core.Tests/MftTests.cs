using System.Buffers.Binary;
using System.Text;
using PcOrg.Core.Indexing.Ntfs;

namespace PcOrg.Core.Tests;

/// <summary>Lettura della Master File Table con record NTFS costruiti a mano (formato reale, settori da 512 byte).</summary>
public class MftTests
{
    private const int RecordSize = 1024, Sector = 512;
    private static readonly long Modified = new DateTime(2025, 3, 14, 10, 30, 0, DateTimeKind.Utc).ToFileTimeUtc();

    /// <summary>Costruisce un record FILE con i fixup applicati come su disco.</summary>
    private static byte[] Record(ulong number, bool directory, IEnumerable<byte[]> attributes, ulong baseRecord = 0, bool inUse = true)
    {
        var r = new byte[RecordSize];
        "FILE"u8.CopyTo(r);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), 0x30);           // offset della sequenza di aggiornamento
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(6), 3);              // 1 + numero di settori
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x14), 0x38);        // primo attributo
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x16), (ushort)((inUse ? 1 : 0) | (directory ? 2 : 0)));
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(0x20), baseRecord);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(0x2C), (uint)number);
        var offset = 0x38;
        foreach (var a in attributes)
        {
            a.CopyTo(r, offset);
            offset += a.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(offset), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(0x18), (uint)(offset + 8));
        // Sequenza di aggiornamento: il numero 0x0042 va negli ultimi due byte di ogni settore.
        const ushort usn = 0x0042;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x30), usn);
        for (var i = 1; i <= 2; i++)
        {
            var end = i * Sector - 2;
            r[0x30 + i * 2] = r[end];
            r[0x30 + i * 2 + 1] = r[end + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(end), usn);
        }
        return r;
    }

    private static byte[] Resident(uint type, byte[] value, string? name = null)
    {
        var nameBytes = name is null ? [] : Encoding.Unicode.GetBytes(name);
        var valueOffset = 0x18 + nameBytes.Length;
        var length = (valueOffset + value.Length + 7) / 8 * 8;
        var a = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(a, type);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)length);
        a[9] = (byte)(name?.Length ?? 0);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(10), 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(0x10), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(0x14), (ushort)valueOffset);
        nameBytes.CopyTo(a, 0x18);
        value.CopyTo(a, valueOffset);
        return a;
    }

    private static byte[] StandardInformation(FileAttributes attributes = FileAttributes.Archive)
    {
        var v = new byte[48];
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(8), Modified);
        BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(32), (uint)attributes);
        return Resident(0x10, v);
    }

    private static byte[] FileName(ulong parent, string name, byte ns = 1)
    {
        var v = new byte[66 + name.Length * 2];
        BinaryPrimitives.WriteUInt64LittleEndian(v, parent | (1UL << 48)); // numero di sequenza nei 16 bit alti
        v[64] = (byte)name.Length;
        v[65] = ns;
        Encoding.Unicode.GetBytes(name).CopyTo(v, 66);
        return Resident(0x30, v);
    }

    private static byte[] NonResidentData(long size, byte[] runs)
    {
        var length = (0x40 + runs.Length + 1 + 7) / 8 * 8;
        var a = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(a, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)length);
        a[8] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(0x20), 0x40);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x28), (size + 4095) / 4096 * 4096);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x30), size);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(0x38), size);
        runs.CopyTo(a, 0x40);
        return a;
    }

    private static MftRecord Parse(byte[] record, ulong number) => MftParser.Parse(record, Sector, number)!;

    [Fact]
    public void Legge_nome_data_dimensione_e_attributi()
    {
        var file = Parse(Record(40, false, [StandardInformation(), FileName(5, "SETUP~1.EXE", 2), FileName(5, "Setup Firefox.exe"),
            Resident(0x80, new byte[234])]), 40);
        Assert.True(file.InUse);
        Assert.False(file.IsDirectory);
        Assert.Equal(234, file.DataSize);
        Assert.Equal(Modified, file.ModifiedFileTime);
        Assert.Equal(2, file.Names.Count);
        Assert.Equal(5UL, file.Names[1].ParentFrn); // il numero di sequenza viene tolto
        Assert.Equal("Setup Firefox.exe", file.Names[1].Name);

        var big = Parse(Record(41, false, [StandardInformation(), FileName(5, "Win11.iso"),
            NonResidentData(5_800_000_000, [0x31, 0x10, 0x00, 0x10, 0x00])]), 41);
        Assert.Equal(5_800_000_000, big.DataSize);
        Assert.Equal([(0x1000L, 0x10L)], big.DataRuns);
    }

    [Fact]
    public void Scarta_i_record_scritti_a_meta()
    {
        var record = Record(42, false, [StandardInformation(), FileName(5, "a.txt")]);
        record[Sector - 1] ^= 0xFF; // il settore non termina con il numero di sequenza atteso
        Assert.Null(MftParser.Parse(record, Sector, 42));
        Assert.Null(MftParser.Parse(new byte[RecordSize], Sector, 43)); // record mai usato
    }

    [Fact]
    public void Interpreta_le_estensioni_con_spostamento_negativo()
    {
        var runs = new List<(long, long)>();
        // 0x20 cluster a LCN 0x5000, poi 0x08 cluster 0x1000 cluster più indietro (0xF000 = -0x1000), poi una zona sparsa.
        MftParser.ParseRuns([0x21, 0x20, 0x00, 0x50, 0x21, 0x08, 0x00, 0xF0, 0x01, 0x04, 0x00], runs);
        Assert.Equal([(0x5000L, 0x20L), (0x4000L, 0x08L)], runs);
    }

    [Fact]
    public void Ricostruisce_le_cartelle_come_il_file_system()
    {
        const ulong users = 30, download = 31, link = 32;
        var records = new[]
        {
            Record(5, true, [StandardInformation(FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System), FileName(5, ".")]),
            Record(users, true, [StandardInformation(FileAttributes.Directory), FileName(5, "Users")]),
            Record(download, true, [StandardInformation(FileAttributes.Directory), FileName(users, "Download")]),
            Record(link, true, [StandardInformation(FileAttributes.Directory | FileAttributes.ReparsePoint), FileName(users, "Collegamento"),
                Resident(0xC0, [0x03, 0x00, 0x00, 0xA0, 0, 0, 0, 0])]),
            Record(40, false, [StandardInformation(), FileName(download, "driver.zip"), Resident(0x80, new byte[300])]),
            Record(41, false, [StandardInformation(FileAttributes.System | FileAttributes.Hidden), FileName(download, "desktop.ini"), Resident(0x80, new byte[10])]),
            Record(42, false, [StandardInformation(), FileName(download, "vecchio.txt")], inUse: false),
            // File con un collegamento fisico in un'altra cartella; il secondo nome sta in un record di estensione.
            Record(43, false, [StandardInformation(), FileName(download, "foto.jpg"), Resident(0x80, new byte[700])]),
            Record(44, false, [FileName(users, "foto-copia.jpg")], baseRecord: 43),
        }.Select((r, i) => MftParser.Parse(r, Sector, BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(0x2C)))!).ToList();

        var snapshot = MftSnapshot.Build(@"C:\", records);
        var (files, subdirs) = snapshot.List(@"C:\Users\Download", default);
        Assert.Equal([@"C:\Users\Download\driver.zip", @"C:\Users\Download\foto.jpg"], files.Select(f => f.Path).Order());
        Assert.Equal(300, files.Single(f => f.Path.EndsWith("driver.zip")).Size);
        Assert.Equal(DateTime.FromFileTimeUtc(Modified), files[0].ModifiedUtc);
        Assert.Empty(subdirs);

        var (usersFiles, usersDirs) = snapshot.List(@"c:\users", default); // maiuscole e minuscole indifferenti
        Assert.Equal([@"c:\users\Download"], usersDirs); // la junction "Collegamento" non si segue
        Assert.Equal(700, Assert.Single(usersFiles).Size);

        Assert.NotNull(snapshot.GetDirectoryTime(@"C:\Users\Download", default));
        Assert.Null(snapshot.GetDirectoryTime(@"C:\Users\Inesistente", default));
        Assert.True(snapshot.Covers(@"C:\Users"));
        Assert.False(snapshot.Covers(@"D:\Dati"));
    }

    [Fact]
    public void La_scansione_dalla_mft_aggiorna_l_indice()
    {
        using var dir = new TempDir();
        using var index = new DisposableIndex(Path.Combine(dir.Path, "indice.db"));
        var root = index.Index.AddRoot(dir.Dir("Dati"));
        var records = new[]
        {
            Record(5, true, [StandardInformation(FileAttributes.Directory), FileName(5, ".")]),
            Record(30, true, [StandardInformation(FileAttributes.Directory), FileName(5, "Dati")]),
            Record(31, true, [StandardInformation(FileAttributes.Directory), FileName(30, "Sotto")]),
            Record(40, false, [StandardInformation(), FileName(30, "setup.exe"), Resident(0x80, new byte[100])]),
            Record(41, false, [StandardInformation(), FileName(31, "nota.txt"), Resident(0x80, new byte[5])]),
        }.Select(r => MftParser.Parse(r, Sector, BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(0x2C)))!);
        // La fotografia descrive un volume la cui radice è la cartella temporanea del test.
        var volume = Path.GetDirectoryName(root.Path)!;
        var snapshot = MftSnapshot.Build(volume, records);

        var result = index.Index.UpdateRoot(root.Id, root.Path, full: true, source: snapshot);
        Assert.Equal(2, result.FilesAdded);
        Assert.Equal(2, result.DirectoriesVisited);
        Assert.Equal(2, index.Index.Search(new Storage.SearchQuery(null)).Count);
    }

    private sealed class DisposableIndex(string path) : IDisposable
    {
        public Storage.FileIndex Index { get; } = Storage.FileIndex.Open(path);
        public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}

/// <summary>Prova con la MFT vera del disco (solo su Windows come amministratore, es. nella build su GitHub).</summary>
public class MftRealVolumeTests
{
    [Fact]
    public void La_mft_vera_coincide_con_il_file_system()
    {
        var platform = Platform.PlatformServices.Current;
        if (!OperatingSystem.IsWindows() || !platform.IsElevated)
            return;
        // Cartella del runtime .NET: file stabili, non toccati durante il test.
        var folder = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory().TrimEnd('\\');
        var snapshot = platform.ReadVolumeSnapshot(folder, default);
        if (snapshot is null)
            return; // volume non NTFS
        var (files, _) = snapshot.List(folder, default);
        var expected = new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { AttributesToSkip = FileAttributes.System })
            .ToDictionary(f => f.Name, f => f.Length, StringComparer.OrdinalIgnoreCase);
        var actual = files.ToDictionary(f => Path.GetFileName(f.Path), f => f.Size, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (name, size) in expected)
            Assert.Equal(size, actual[name]);
        Assert.NotNull(snapshot.GetDirectoryTime(folder, default));
    }
}
