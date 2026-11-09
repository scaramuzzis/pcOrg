using System.IO.Enumeration;

namespace PcOrg.Core.Indexing;

/// <summary>File trovato durante la scansione.</summary>
public readonly record struct ScannedFile(string Path, long Size, DateTime ModifiedUtc);

/// <summary>Enumerazione veloce e ricorsiva dei file di una cartella.</summary>
public static class FolderScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Salta file di sistema e collegamenti a cartelle (evita cicli e duplicati).
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public static IEnumerable<ScannedFile> Enumerate(string root, CancellationToken cancellationToken = default)
    {
        var enumerable = new FileSystemEnumerable<ScannedFile>(
            root,
            (ref FileSystemEntry e) => new ScannedFile(e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcDateTime),
            Options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
        };

        foreach (var file in enumerable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }
}
