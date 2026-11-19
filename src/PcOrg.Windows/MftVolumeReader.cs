using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using PcOrg.Core;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Indexing.Ntfs;

namespace PcOrg.Windows;

/// <summary>
/// Legge la Master File Table di un volume NTFS direttamente dal disco (come "Everything"): pochi grandi
/// blocchi sequenziali invece di aprire una cartella alla volta. Richiede che Gestione File sia avviato
/// come amministratore; altrimenti (o su volumi non NTFS, di rete o se qualcosa non torna) restituisce
/// null e la scansione procede nel modo normale.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MftVolumeReader
{
    private const uint GenericRead = 0x80000000, FileShareAll = 0x7, OpenExisting = 3;
    private const uint FsctlGetNtfsVolumeData = 0x00090064;
    private const int ChunkBytes = 4 << 20;

    public static MftSnapshot? TryRead(string path, CancellationToken cancellationToken)
    {
        if (NetworkPaths.IsNetworkPath(path) || Path.GetPathRoot(path) is not { Length: >= 2 } root || root[1] != ':')
            return null;
        try
        {
            if (!new DriveInfo(root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                return null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }

        using var handle = CreateFileW(@"\\.\" + root[..2], GenericRead, FileShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            Log.Info($"Scansione veloce NTFS non disponibile per {root} (serve avviare Gestione File come amministratore).");
            return null;
        }
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var snapshot = Read(handle, root, cancellationToken);
            if (snapshot is not null)
                Log.Info($"MFT di {root} letta in {stopwatch.Elapsed.TotalSeconds:N1} s: {snapshot.EntryCount:N0} file e cartelle.");
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            Log.Warn($"Lettura della MFT di {root} non riuscita: si usa la scansione normale.", ex);
            return null;
        }
    }

    private static MftSnapshot? Read(SafeFileHandle handle, string root, CancellationToken cancellationToken)
    {
        var data = new byte[128];
        if (!DeviceIoControl(handle, FsctlGetNtfsVolumeData, IntPtr.Zero, 0, data, data.Length, out _, IntPtr.Zero))
            return null;
        var bytesPerSector = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40));
        var bytesPerCluster = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44));
        var bytesPerRecord = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(48));
        var mftValidBytes = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(56));
        var mftStartLcn = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(64));
        if (bytesPerSector <= 0 || bytesPerCluster <= 0 || bytesPerRecord <= 0 || bytesPerCluster % bytesPerSector != 0)
            throw new InvalidDataException("Parametri del volume NTFS non validi.");

        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0, isAsync: false);

        // Record 0 ($MFT): le sue estensioni dicono dove si trova sul disco il resto della tabella.
        var firstSize = Math.Max(bytesPerCluster, (bytesPerRecord + bytesPerSector - 1) / bytesPerSector * bytesPerSector);
        var first = new byte[firstSize];
        ReadAt(stream, mftStartLcn * bytesPerCluster, first, first.Length);
        var mft = MftParser.Parse(first.AsSpan(0, bytesPerRecord), bytesPerSector, 0, readRuns: true);
        if (mft is null || mft.DataRuns.Count == 0)
            throw new InvalidDataException("Record $MFT non leggibile.");

        var records = new List<MftRecord>((int)Math.Min(int.MaxValue / 2, mftValidBytes / bytesPerRecord));
        var chunk = ChunkBytes / bytesPerCluster * bytesPerCluster;
        chunk = Math.Max(chunk / bytesPerRecord * bytesPerRecord, bytesPerCluster);
        var buffer = new byte[chunk];
        ulong number = 0;
        var remaining = mftValidBytes;
        foreach (var (lcn, clusters) in mft.DataRuns)
        {
            var offset = lcn * bytesPerCluster;
            var bytes = clusters * bytesPerCluster;
            while (bytes > 0 && remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var n = (int)Math.Min(chunk, bytes);
                ReadAt(stream, offset, buffer, n);
                var usable = (int)Math.Min(n, remaining);
                for (var i = 0; i + bytesPerRecord <= usable; i += bytesPerRecord)
                {
                    var record = MftParser.Parse(buffer.AsSpan(i, bytesPerRecord), bytesPerSector, number++, readRuns: false);
                    if (record is { InUse: true })
                        records.Add(record);
                }
                offset += n;
                bytes -= n;
                remaining -= n;
            }
        }
        return MftSnapshot.Build(root, records);
    }

    private static void ReadAt(FileStream stream, long offset, byte[] buffer, int count)
    {
        stream.Position = offset;
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n <= 0)
                throw new IOException("Fine del volume inattesa leggendo la MFT.");
            read += n;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, int inBufferSize,
        byte[] outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
