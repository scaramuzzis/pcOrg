namespace PcOrg.Core.Indexing.Ntfs;

/// <summary>
/// Fotografia di un volume NTFS ricavata dalla Master File Table: per ogni cartella i file con dimensione e
/// data, le sottocartelle e la data della cartella. La scansione la interroga come se fosse il disco
/// (<see cref="IDirectorySource"/>), ma senza aprire una cartella alla volta: con milioni di file è molte
/// volte più veloce dell'enumerazione normale.
/// </summary>
public sealed class MftSnapshot : IDirectorySource
{
    /// <summary>Record della cartella radice del volume.</summary>
    public const ulong RootFrn = 5;

    private const uint ReparseTagMountPoint = 0xA0000003, ReparseTagSymlink = 0xA000000C;

    private sealed class Node
    {
        public required string Name;
        public bool IsDirectory;
        public long Size;
        public long ModifiedFileTime;
        public FileAttributes Attributes;
        public uint? ReparseTag;
        public List<Node>? Children;
    }

    private readonly string _volumeRoot;
    /// <summary>Separatore dei percorsi: "\" su Windows (nei test può essere "/").</summary>
    private readonly char _separator;
    private readonly Node _root;
    private readonly Dictionary<string, Node?> _byPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Numero di file e cartelle in uso trovati nella MFT.</summary>
    public int EntryCount { get; }

    private MftSnapshot(string volumeRoot, Node root, int count)
    {
        _separator = volumeRoot.Contains('/') ? '/' : '\\';
        _volumeRoot = volumeRoot.EndsWith(_separator) ? volumeRoot : volumeRoot + _separator;
        _root = root;
        EntryCount = count;
        _byPath[_volumeRoot.TrimEnd(_separator)] = root;
        _byPath[_volumeRoot] = root;
    }

    /// <summary>Costruisce la fotografia dai record letti (record base ed estensioni, in qualsiasi ordine).</summary>
    /// <param name="volumeRoot">Radice del volume, es. "C:\".</param>
    public static MftSnapshot Build(string volumeRoot, IEnumerable<MftRecord> records)
    {
        // 1. Si riuniscono i record di estensione nel loro record base.
        var bases = new Dictionary<ulong, MftRecord>();
        var extensions = new List<MftRecord>();
        foreach (var r in records)
        {
            if (!r.InUse)
                continue;
            if (r.BaseRecord != 0 && r.BaseRecord != r.Number)
                extensions.Add(r);
            else
                bases[r.Number] = r;
        }
        foreach (var ext in extensions)
        {
            if (!bases.TryGetValue(ext.BaseRecord, out var b))
                continue;
            b.Names.AddRange(ext.Names);
            b.DataSize ??= ext.DataSize;
            b.ReparseTag ??= ext.ReparseTag;
            b.ModifiedFileTime ??= ext.ModifiedFileTime;
            b.Attributes ??= ext.Attributes;
        }

        // 2. Un nodo per record; un file con più collegamenti fisici compare in ogni cartella che lo contiene.
        var nodes = new Dictionary<ulong, Node>(bases.Count);
        foreach (var (number, r) in bases)
            nodes[number] = new Node
            {
                Name = "",
                IsDirectory = r.IsDirectory,
                Size = r.IsDirectory ? 0 : r.DataSize ?? 0,
                ModifiedFileTime = r.ModifiedFileTime ?? 0,
                Attributes = r.Attributes ?? 0,
                ReparseTag = r.ReparseTag,
            };
        if (!nodes.TryGetValue(RootFrn, out var root))
            root = new Node { Name = "", IsDirectory = true };

        foreach (var (number, r) in bases)
        {
            if (number == RootFrn)
                continue;
            var node = nodes[number];
            // Nomi lunghi (Win32/POSIX); il nome corto DOS solo se non c'è altro.
            var names = r.Names.Where(n => n.Namespace != 2).ToList();
            if (names.Count == 0)
                names = r.Names;
            foreach (var group in names.GroupBy(n => (n.ParentFrn, n.Name.ToUpperInvariant())))
            {
                var name = group.First();
                if (!nodes.TryGetValue(name.ParentFrn, out var parent) || !parent.IsDirectory || ReferenceEquals(parent, node))
                    continue;
                var entry = names.Count == 1 ? node : Clone(node);
                entry.Name = name.Name;
                (parent.Children ??= []).Add(entry);
            }
        }
        return new MftSnapshot(volumeRoot, root, nodes.Count);
    }

    private static Node Clone(Node n) => new()
    {
        Name = n.Name, IsDirectory = n.IsDirectory, Size = n.Size, ModifiedFileTime = n.ModifiedFileTime,
        Attributes = n.Attributes, ReparseTag = n.ReparseTag, Children = n.Children,
    };

    /// <summary>Vero se il percorso è su questo volume.</summary>
    public bool Covers(string path) => path.StartsWith(_volumeRoot, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(path, _volumeRoot.TrimEnd(_separator), StringComparison.OrdinalIgnoreCase);

    private Node? Find(string dir)
    {
        dir = dir.TrimEnd(_separator);
        if (dir.Length == 0)
            return null;
        if (_byPath.TryGetValue(dir, out var cached))
            return cached;
        Node? result = null;
        var cut = dir.LastIndexOf(_separator);
        if (cut > 0 && Covers(dir) && Find(dir[..cut]) is { Children: { } children })
        {
            var name = dir[(cut + 1)..];
            result = children.FirstOrDefault(c => c.IsDirectory && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        _byPath[dir] = result;
        return result;
    }

    public long? GetDirectoryTime(string dir, CancellationToken cancellationToken) =>
        Find(dir) is { IsDirectory: true } node ? ToUnixMilliseconds(node.ModifiedFileTime) : null;

    public (IReadOnlyList<ScannedFile> Files, IReadOnlyList<string> Subdirectories) List(string dir, CancellationToken cancellationToken)
    {
        if (Find(dir) is not { IsDirectory: true } node)
            throw new DirectoryNotFoundException(dir);
        var files = new List<ScannedFile>();
        var subdirs = new List<string>();
        var basePath = dir.EndsWith(_separator) ? dir : dir + _separator;
        foreach (var child in node.Children ?? [])
        {
            // Come l'enumerazione normale: niente file di sistema né collegamenti a cartelle.
            if ((child.Attributes & FileAttributes.System) != 0)
                continue;
            if (child.IsDirectory)
            {
                if (child.ReparseTag is ReparseTagMountPoint or ReparseTagSymlink)
                    continue;
                subdirs.Add(basePath + child.Name);
            }
            else
            {
                files.Add(new ScannedFile(basePath + child.Name, child.Size, FromFileTime(child.ModifiedFileTime)));
            }
        }
        return (files, subdirs);
    }

    private static DateTime FromFileTime(long fileTime) =>
        fileTime <= 0 ? DateTime.UnixEpoch : DateTime.FromFileTimeUtc(fileTime);

    private static long ToUnixMilliseconds(long fileTime) =>
        new DateTimeOffset(FromFileTime(fileTime)).ToUnixTimeMilliseconds();
}
