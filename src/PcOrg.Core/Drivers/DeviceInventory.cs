using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
        if (!OperatingSystem.IsWindows())
            return [];
        try
        {
            var devices = ReadWindows();
            Log.Info($"Periferiche presenti lette: {devices.Count}.");
            return devices;
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn("Elenco delle periferiche non leggibile.", ex);
            return [];
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<Device> ReadWindows()
    {
        const uint DigcfPresent = 0x2, DigcfAllClasses = 0x4;
        var handle = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (handle == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var list = new List<Device>();
        try
        {
            var data = new SpDevInfoData { cbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(handle, i, ref data); i++)
            {
                var hardware = MultiString(handle, ref data, SpdrpHardwareId);
                if (hardware.Count == 0)
                    continue; // periferiche software senza identificativi: non interessano
                var name = SingleString(handle, ref data, SpdrpFriendlyName) ?? SingleString(handle, ref data, SpdrpDeviceDesc) ?? hardware[0];
                list.Add(new Device(name, SingleString(handle, ref data, SpdrpClass), hardware,
                    MultiString(handle, ref data, SpdrpCompatibleIds),
                    DeviceProperty(handle, ref data, DriverVersionKey), DeviceProperty(handle, ref data, DriverProviderKey)));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(handle);
        }
        return list;
    }

    // ------------------------------------------------------------------ SetupAPI

    private const uint SpdrpDeviceDesc = 0, SpdrpHardwareId = 1, SpdrpCompatibleIds = 2, SpdrpClass = 7, SpdrpFriendlyName = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid fmtid;
        public uint pid;
    }

    private static readonly DevPropKey DriverVersionKey = new() { fmtid = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), pid = 3 };
    private static readonly DevPropKey DriverProviderKey = new() { fmtid = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), pid = 9 };

    private static byte[]? RegistryProperty(IntPtr handle, ref SpDevInfoData data, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(handle, ref data, property, out _, null, 0, out var required);
        if (required == 0)
            return null;
        var buffer = new byte[required];
        return SetupDiGetDeviceRegistryPropertyW(handle, ref data, property, out _, buffer, required, out _) ? buffer : null;
    }

    private static string? SingleString(IntPtr handle, ref SpDevInfoData data, uint property) =>
        RegistryProperty(handle, ref data, property) is { } bytes ? System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0') : null;

    private static List<string> MultiString(IntPtr handle, ref SpDevInfoData data, uint property) =>
        RegistryProperty(handle, ref data, property) is { } bytes
            ? System.Text.Encoding.Unicode.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList()
            : [];

    private static string? DeviceProperty(IntPtr handle, ref SpDevInfoData data, DevPropKey key)
    {
        SetupDiGetDevicePropertyW(handle, ref data, ref key, out _, null, 0, out var required, 0);
        if (required == 0)
            return null;
        var buffer = new byte[required];
        return SetupDiGetDevicePropertyW(handle, ref data, ref key, out _, buffer, required, out _, 0)
            ? System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0')
            : null;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData,
        uint property, out uint propertyRegDataType, byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData,
        ref DevPropKey propertyKey, out uint propertyType, byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
