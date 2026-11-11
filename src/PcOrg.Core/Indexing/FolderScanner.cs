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
        // Salta i file di sistema. I "reparse point" non si scartano (i file di OneDrive lo sono):
        // i collegamenti a cartelle vengono esclusi in ShouldRecursePredicate.
        AttributesToSkip = FileAttributes.System,
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
            // Non si entra nei collegamenti a cartelle (junction, link simbolici): cicli e doppioni.
            ShouldRecursePredicate = (ref FileSystemEntry e) =>
                (e.Attributes & FileAttributes.ReparsePoint) == 0 || e.ToFileSystemInfo().LinkTarget is null,
        };

        foreach (var file in enumerable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }
}
