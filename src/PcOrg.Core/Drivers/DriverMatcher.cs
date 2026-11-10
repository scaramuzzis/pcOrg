using PcOrg.Core.Metadata;

namespace PcOrg.Core.Drivers;

/// <summary>Confronto fra un driver dell'archivio e il driver installato per una periferica.</summary>
public enum DriverState
{
    /// <summary>Nessuna periferica di questo PC usa questo driver.</summary>
    NonServe,
    /// <summary>È la stessa versione già installata.</summary>
    StessaVersione,
    /// <summary>Più recente di quello installato: è un aggiornamento.</summary>
    PiuRecente,
    /// <summary>Più vecchio di quello installato.</summary>
    Superato,
    /// <summary>La periferica lo può usare, ma non è possibile confrontare le versioni.</summary>
    Compatibile,
}

/// <summary>Risultato del confronto di un driver con le periferiche presenti.</summary>
public sealed record DriverMatch(DriverState State, IReadOnlyList<Device> Devices, string? DriverVersion)
{
    /// <summary>Periferica di riferimento (la prima trovata).</summary>
    public Device? Device => Devices.Count > 0 ? Devices[0] : null;

    public string Text
    {
        get
        {
            if (Device is null)
                return "Non serve a questo PC";
            var who = Devices.Count == 1 ? Device.Name : $"{Device.Name} e altre {Devices.Count - 1}";
            return State switch
            {
                DriverState.StessaVersione => $"Per {who}: è quello installato",
                DriverState.PiuRecente => $"Per {who}: più recente dell'installato ({Device.DriverVersion})",
                DriverState.Superato => $"Per {who}: superato (installato {Device.DriverVersion})",
                _ => $"Per {who}",
            };
        }
    }
}

/// <summary>
/// Confronta gli identificativi hardware dei driver con quelli delle periferiche presenti.
/// Si usano sia gli identificativi hardware sia quelli compatibili delle periferiche, come fa Windows
/// per scegliere i driver installabili.
/// </summary>
public sealed class DriverMatcher
{
    private readonly Dictionary<string, List<Device>> _byId = new(StringComparer.OrdinalIgnoreCase);

    public static DriverMatcher Empty { get; } = new([]);

    public IReadOnlyList<Device> Devices { get; }

    public DriverMatcher(IReadOnlyList<Device> devices)
    {
        Devices = devices;
        foreach (var device in devices)
            foreach (var id in device.HardwareIds.Concat(device.CompatibleIds).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!_byId.TryGetValue(id, out var list))
                    _byId[id] = list = [];
                list.Add(device);
            }
    }

    /// <summary>Periferiche presenti che il driver supporta, e confronto di versione con il driver installato.</summary>
    public DriverMatch Match(IEnumerable<DriverHardwareId> ids, string? driverVersion)
    {
        var devices = ids.SelectMany(i => _byId.GetValueOrDefault(i.Id) ?? []).Distinct().ToList();
        if (devices.Count == 0)
            return new DriverMatch(DriverState.NonServe, [], driverVersion);

        // Si confronta con la periferica il cui driver installato è più simile (stesso fornitore se possibile).
        var device = devices.OrderByDescending(d => d.DriverVersion is not null).First();
        if (driverVersion is null || device.DriverVersion is null)
            return new DriverMatch(DriverState.Compatibile, devices, driverVersion);
        var cmp = VersionComparer.Instance.Compare(driverVersion, device.DriverVersion);
        return new DriverMatch(cmp switch { 0 => DriverState.StessaVersione, > 0 => DriverState.PiuRecente, _ => DriverState.Superato },
            devices, driverVersion);
    }
}
