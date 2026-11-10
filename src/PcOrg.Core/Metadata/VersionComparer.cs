using System.Text.RegularExpressions;

namespace PcOrg.Core.Metadata;

/// <summary>
/// Confronta versioni scritte come testo ("566.36", "3.0.21.0", "24.08 beta").
/// Si confronta la prima sequenza numerica puntata; gli zeri finali non contano (3.0.21 = 3.0.21.0).
/// </summary>
public sealed partial class VersionComparer : IComparer<string?>
{
    public static VersionComparer Instance { get; } = new();

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")]
    private static partial Regex NumericVersion();

    public int Compare(string? x, string? y)
    {
        var a = Parse(x);
        var b = Parse(y);
        if (a is null || b is null)
            return (a is null).CompareTo(b is null) * -1; // le versioni sconosciute vanno per ultime
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var left = i < a.Length ? a[i] : 0;
            var right = i < b.Length ? b[i] : 0;
            if (left != right)
                return left.CompareTo(right);
        }
        return 0;
    }

    public static bool AreEqual(string? x, string? y) => Instance.Compare(x, y) == 0;

    /// <summary>Parti numeriche della versione, o null se il testo non contiene numeri.</summary>
    public static long[]? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = NumericVersion().Match(text);
        if (!match.Success)
            return null;
        var parts = match.Value.Split('.', ',');
        var numbers = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            numbers[i] = long.TryParse(parts[i], out var n) ? n : 0;
        return numbers;
    }

    /// <summary>Pulisce una versione per la visualizzazione ("10.0.19041.1 (WinBuild...)" → "10.0.19041.1").</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = NumericVersion().Match(text);
        return match.Success ? match.Value.Replace(',', '.') : text.Trim();
    }
}
