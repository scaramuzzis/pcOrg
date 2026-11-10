using System.Runtime.Versioning;
using Microsoft.Win32;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Metadata;
using PcOrg.Core.Storage;

namespace PcOrg.Core.Installed;

/// <summary>Programma installato sul PC, come compare in "App installate" di Windows.</summary>
public sealed record InstalledProgram(string Name, string? Version, string? Publisher, string? InstallLocation)
{
    /// <summary>Chiave di confronto con gli installer (nome normalizzato e produttore), come per i prodotti.</summary>
    public string? Key { get; init; }
}

/// <summary>Esito del confronto fra un installer e i programmi installati.</summary>
public enum InstallState
{
    /// <summary>Non è un installer o non è stato possibile capirlo.</summary>
    Sconosciuto = 0,
    NonInstallato,
    /// <summary>Installata la stessa versione dell'installer.</summary>
    StessaVersione,
    /// <summary>Installata una versione più vecchia: l'installer serve ad aggiornare.</summary>
    InstallataPiuVecchia,
    /// <summary>Installata una versione più recente: l'installer è superato.</summary>
    InstallataPiuRecente,
    /// <summary>Installato, ma senza versione confrontabile.</summary>
    Installato,
}

/// <summary>Risultato del confronto per un file.</summary>
public sealed record InstallInfo(InstallState State, InstalledProgram? Program)
{
    public string Text => State switch
    {
        InstallState.NonInstallato => "Non installato",
        InstallState.StessaVersione => $"Installato ({Program?.Version})",
        InstallState.InstallataPiuVecchia => $"Installata {Program?.Version}: questo è più recente",
        InstallState.InstallataPiuRecente => $"Installata {Program?.Version}: questo è superato",
        InstallState.Installato => "Installato",
        _ => "",
    };
}

/// <summary>
/// Legge i programmi installati dalle chiavi "Uninstall" del registro (le stesse usate da
/// "App installate") e li confronta con gli installer presenti nelle cartelle monitorate.
/// </summary>
/// <remarks>
/// Si leggono HKLM (programmi a 64 e 32 bit) e HKCU (installati solo per l'utente). Vengono
/// scartati i componenti di sistema e gli aggiornamenti (voci con SystemComponent=1 o con
/// un programma "padre"). Il confronto usa la stessa normalizzazione del nome usata per i prodotti
/// (<see cref="ProductKeys"/>): "VLC media player 3.0.21" e "VLC media player" coincidono.
/// </remarks>
public static class InstalledPrograms
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Programmi installati (vuoto fuori da Windows o se il registro non è leggibile).</summary>
    public static IReadOnlyList<InstalledProgram> Read()
    {
        if (!OperatingSystem.IsWindows())
            return [];
        var list = new List<InstalledProgram>();
        try
        {
            ReadHive(RegistryHive.LocalMachine, RegistryView.Registry64, list);
            ReadHive(RegistryHive.LocalMachine, RegistryView.Registry32, list);
            ReadHive(RegistryHive.CurrentUser, RegistryView.Default, list);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn("Elenco dei programmi installati non leggibile completamente.", ex);
        }
        var distinct = list
            .GroupBy(p => (p.Name.ToUpperInvariant(), p.Version))
            .Select(g => g.First())
            .ToList();
        Log.Info($"Programmi installati letti dal registro: {distinct.Count}.");
        return distinct;
    }

    [SupportedOSPlatform("windows")]
    private static void ReadHive(RegistryHive hive, RegistryView view, List<InstalledProgram> list)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var uninstall = root.OpenSubKey(UninstallKey);
        if (uninstall is null)
            return;
        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var key = uninstall.OpenSubKey(name);
            if (key?.GetValue("DisplayName") is not string display || string.IsNullOrWhiteSpace(display))
                continue;
            if (key.GetValue("SystemComponent") is int system && system == 1)
                continue;
            if (key.GetValue("ParentKeyName") is string || key.GetValue("ReleaseType") is string release && release.Contains("Update", StringComparison.OrdinalIgnoreCase))
                continue;
            list.Add(Create(display.Trim(), key.GetValue("DisplayVersion") as string, key.GetValue("Publisher") as string,
                key.GetValue("InstallLocation") as string));
        }
    }

    /// <summary>Crea la voce calcolando la chiave di confronto (pubblico per i test).</summary>
    public static InstalledProgram Create(string name, string? version, string? publisher, string? location = null)
    {
        var program = new InstalledProgram(name, string.IsNullOrWhiteSpace(version) ? null : version.Trim(), publisher?.Trim(), location);
        return program with { Key = ProductKeys.For(new FileMeta { ProductName = name, CompanyName = publisher }, name) };
    }

    /// <summary>
    /// Confronta un file (installer o programma) con i programmi installati. Si cerca prima per chiave
    /// completa (nome e produttore), poi per solo nome se il produttore manca o è scritto diversamente.
    /// </summary>
    public static InstallInfo Match(FileRecord file, IReadOnlyList<InstalledProgram> installed)
    {
        if (file.Meta?.ProductKey is not { } key || file.Category is not (Classification.FileCategory.Installer or Classification.FileCategory.Driver))
            return new InstallInfo(InstallState.Sconosciuto, null);

        var name = key.Split('|')[0];
        var candidates = installed.Where(p => p.Key == key).ToList();
        if (candidates.Count == 0)
            candidates = installed.Where(p => p.Key is not null && p.Key.Split('|')[0] == name).ToList();
        if (candidates.Count == 0)
            return new InstallInfo(InstallState.NonInstallato, null);

        // Se ci sono più installazioni (es. 32 e 64 bit) si confronta con la più recente.
        var program = candidates.OrderByDescending(p => p.Version, VersionComparer.Instance).First();
        if (file.Version is null || program.Version is null)
            return new InstallInfo(InstallState.Installato, program);
        var cmp = VersionComparer.Instance.Compare(file.Version, program.Version);
        return new InstallInfo(cmp switch
        {
            0 => InstallState.StessaVersione,
            > 0 => InstallState.InstallataPiuVecchia,
            _ => InstallState.InstallataPiuRecente,
        }, program);
    }
}

/// <summary>
/// Elenco dei programmi installati indicizzato per chiave, per confrontare velocemente
/// migliaia di file (usato dall'interfaccia per le colonne "Sul PC").
/// </summary>
public sealed class InstalledIndex
{
    private readonly Dictionary<string, List<InstalledProgram>> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<InstalledProgram>> _byName = new(StringComparer.Ordinal);

    public static InstalledIndex Empty { get; } = new([]);

    public IReadOnlyList<InstalledProgram> Programs { get; }

    public InstalledIndex(IReadOnlyList<InstalledProgram> programs)
    {
        Programs = programs;
        foreach (var p in programs.Where(p => p.Key is not null))
        {
            Add(_byKey, p.Key!, p);
            Add(_byName, p.Key!.Split('|')[0], p);
        }
    }

    private static void Add(Dictionary<string, List<InstalledProgram>> map, string key, InstalledProgram p)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        list.Add(p);
    }

    /// <summary>Come <see cref="InstalledPrograms.Match"/>, ma con ricerca per chiave invece che lineare.</summary>
    public InstallInfo Match(FileRecord file)
    {
        if (file.Meta?.ProductKey is not { } key)
            return new InstallInfo(InstallState.Sconosciuto, null);
        var candidates = _byKey.GetValueOrDefault(key) ?? _byName.GetValueOrDefault(key.Split('|')[0]);
        return InstalledPrograms.Match(file, candidates ?? []);
    }
}
