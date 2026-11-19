using PcOrg.Core.Diagnostics;

namespace PcOrg.Core.Drivers;

/// <summary>Periferica presente su questo PC, come in Gestione dispositivi.</summary>
public sealed record Device(
    string Name,
    string? Class,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds,
    string? DriverVersion,
    string? DriverProvider);

/// <summary>
/// Elenco delle periferiche presenti, letto con SetupAPI (la stessa interfaccia di Gestione dispositivi):
/// nome, classe, identificativi hardware e compatibili, versione e fornitore del driver installato.
/// Fuori da Windows l'elenco è vuoto.
/// </summary>
public static class DeviceInventory
{
    public static IReadOnlyList<Device> Read()
    {
        try
        {
            var devices = Platform.PlatformServices.Current.ReadDevices();
            if (devices.Count > 0)
                Log.Info($"Periferiche presenti lette: {devices.Count}.");
            return devices;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn("Elenco delle periferiche non leggibile.", ex);
            return [];
        }
    }
}
