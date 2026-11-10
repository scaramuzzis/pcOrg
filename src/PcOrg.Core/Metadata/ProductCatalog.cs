using PcOrg.Core.Storage;

namespace PcOrg.Core.Metadata;

/// <summary>Le versioni di uno stesso prodotto trovate sul PC.</summary>
public sealed record ProductGroup(
    string Key,
    string Name,
    string? Company,
    IReadOnlyList<FileRecord> Files,
    string? LatestVersion)
{
    /// <summary>Numero di versioni diverse (le copie identiche della stessa versione contano una volta).</summary>
    public int VersionCount => Files.Select(f => VersionComparer.Clean(f.Version) ?? "").Distinct().Count();

    public long TotalSize => Files.Sum(f => f.Size);

    /// <summary>File di versioni precedenti all'ultima.</summary>
    public IEnumerable<FileRecord> OldVersions => Files.Where(IsOld);

    /// <summary>Spazio occupato dalle versioni precedenti all'ultima.</summary>
    public long OldVersionsSize => OldVersions.Sum(f => f.Size);

    public bool IsLatest(FileRecord file) => VersionComparer.AreEqual(file.Version, LatestVersion);

    public bool IsOld(FileRecord file) =>
        LatestVersion is not null && file.Version is not null && VersionComparer.Instance.Compare(file.Version, LatestVersion) < 0;
}

/// <summary>Raggruppa i file per prodotto (stesso nome e produttore, versioni diverse).</summary>
public static class ProductCatalog
{
    public static IReadOnlyList<ProductGroup> Build(IEnumerable<FileRecord> files)
    {
        var groups = new List<ProductGroup>();
        foreach (var group in files.Where(f => f.Meta?.ProductKey is not null).GroupBy(f => f.Meta!.ProductKey!))
        {
            var ordered = group
                .OrderByDescending(f => f.Version, VersionComparer.Instance)
                .ThenByDescending(f => f.ModifiedUtc)
                .ToList();
            var name = MostCommon(ordered.Select(f => f.ProductName ?? f.Meta?.FileDescription))
                ?? Path.GetFileNameWithoutExtension(ordered[0].Name);
            var company = MostCommon(ordered.Select(f => f.Company));
            groups.Add(new ProductGroup(group.Key, name, company, ordered, ordered[0].Version));
        }
        return groups
            .OrderByDescending(g => g.Files.Count > 1)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string? MostCommon(IEnumerable<string?> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v))
              .GroupBy(v => v!, StringComparer.OrdinalIgnoreCase)
              .OrderByDescending(g => g.Count())
              .Select(g => g.Key)
              .FirstOrDefault();
}
