using System.Runtime.InteropServices;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Moving;

/// <summary>Esito dello spostamento nel Cestino.</summary>
public sealed record RecycleResult(IReadOnlyList<string> Recycled, IReadOnlyList<string> Errors)
{
    public long Bytes { get; init; }
}

/// <summary>
/// Sposta i file nel Cestino di Windows (mai eliminazione definitiva): l'utente può sempre ripristinarli
/// dal Cestino. Prima di spostare controlla che il file esista ancora e non sia cambiato.
/// </summary>
public static class RecycleBin
{
    /// <summary>Funzione che sposta davvero un file nel Cestino (sostituibile nei test).</summary>
    public static Func<string, bool> SendToRecycleBin { get; set; } = DefaultSend;

    public static RecycleResult Recycle(FileIndex index, IReadOnlyList<FileRecord> files, CancellationToken cancellationToken = default)
    {
        var recycled = new List<string>();
        var errors = new List<string>();
        long bytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file.Path);
                if (!info.Exists)
                {
                    errors.Add($"{file.Path}: il file non esiste più.");
                    continue;
                }
                if (info.Length != file.Size)
                {
                    errors.Add($"{file.Path}: il file è cambiato dall'ultimo aggiornamento, non è stato toccato.");
                    continue;
                }
                if (!SendToRecycleBin(file.Path))
                {
                    errors.Add($"{file.Path}: Windows non ha potuto spostarlo nel Cestino.");
                    continue;
                }
                recycled.Add(file.Path);
                bytes += file.Size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{file.Path}: {ex.Message}");
            }
        }
        index.RemoveFiles(recycled);
        Log.Info($"Nel Cestino: {recycled.Count} file ({bytes:N0} byte), {errors.Count} non spostati." +
                 (recycled.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, recycled.Select(p => "    " + p)) : ""));
        return new RecycleResult(recycled, errors) { Bytes = bytes };
    }

    // ------------------------------------------------------------------ Windows: SHFileOperation con "annulla"

    private static bool DefaultSend(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Il Cestino è disponibile solo su Windows.");
        var op = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = path + "\0\0", // elenco terminato da doppio carattere nullo
            fFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
    }

    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004, FofNoConfirmation = 0x0010, FofAllowUndo = 0x0040, FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);
}
