namespace PcOrg.Core;

/// <summary>Utilità sui percorsi. I confronti ignorano maiuscole/minuscole, come il file system di Windows.</summary>
public static class PathUtil
{
    public static StringComparison Comparison => StringComparison.OrdinalIgnoreCase;

    /// <summary>Percorso assoluto senza separatore finale (tranne per la radice di un'unità, es. "C:\").</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0)
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
    }

    /// <summary>Vero se <paramref name="path"/> coincide con <paramref name="folder"/> o si trova al suo interno.</summary>
    public static bool IsSameOrUnder(string path, string folder)
    {
        path = Normalize(path);
        folder = Normalize(folder);
        if (path.Equals(folder, Comparison))
            return true;
        var prefix = folder.EndsWith(Path.DirectorySeparatorChar) ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, Comparison);
    }
}
