using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Drivers;
using PcOrg.Core.Installed;
using PcOrg.Core.Metadata;
using PcOrg.Core.Platform;

namespace PcOrg.Windows;

/// <summary>Registrazione della piattaforma Windows (da chiamare all'avvio; altrove non fa nulla).</summary>
public static class WindowsPlatformSetup
{
    public static void Register()
    {
        if (OperatingSystem.IsWindows())
            PlatformServices.Current = new WindowsPlatform();
    }
}

/// <summary>Implementazione per Windows delle funzioni di sistema usate da PcOrg.Core.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IPlatformServices
{
    // ------------------------------------------------------------------ Programmi installati (registro)

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<InstalledProgram> ReadInstalledPrograms()
    {
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
        return list;
    }

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
            list.Add(InstalledPrograms.Create(display.Trim(), key.GetValue("DisplayVersion") as string, key.GetValue("Publisher") as string,
                key.GetValue("InstallLocation") as string));
        }
    }

    // ------------------------------------------------------------------ Periferiche (SetupAPI)

    public IReadOnlyList<Device> ReadDevices() => ReadWindows();

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

    // ------------------------------------------------------------------ Cestino (SHFileOperation con "annulla")

    public bool SendToRecycleBin(string path) => Recycle(path);

    private static bool Recycle(string path)
    {
        var op = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = path + "\0\0", // elenco terminato da doppio carattere nullo
            fFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
    }

    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004, FofNoConfirmation = 0x0010, FofAllowUndo = 0x0040, FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);

    // ------------------------------------------------------------------ Firme digitali

    public int? VerifyAuthenticode(string path) => WinTrust.Verify(path);

    public string? SignerFromFile(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // nessuna alternativa per leggere il certificato da file firmati non PE (MSI)
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return Signature.NameOf(certificate);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static class WinTrust
    {
        public const int TrustENoSignature = unchecked((int)0x800B0100);
        public const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
        public const int TrustEProviderUnknown = unchecked((int)0x800B0001);

        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

        public static int Verify(string path)
        {
            const uint WtdUiNone = 2, WtdRevokeNone = 0, WtdChoiceFile = 1, WtdStateActionIgnore = 0;
            const uint WtdCacheOnlyUrlRetrieval = 0x1000, WtdDisableMd2Md4 = 0x2000;

            var pathPtr = Marshal.StringToCoTaskMemUni(path);
            var filePtr = IntPtr.Zero;
            try
            {
                var file = new WinTrustFileInfo
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    pcwszFilePath = pathPtr,
                };
                filePtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(file, filePtr, false);

                var data = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                    dwUIChoice = WtdUiNone,
                    fdwRevocationChecks = WtdRevokeNone,
                    dwUnionChoice = WtdChoiceFile,
                    pFile = filePtr,
                    dwStateAction = WtdStateActionIgnore,
                    dwProvFlags = WtdCacheOnlyUrlRetrieval | WtdDisableMd2Md4,
                };
                var action = GenericVerifyV2;
                return WinVerifyTrust(new IntPtr(-1), ref action, ref data); // INVALID_HANDLE_VALUE: nessuna UI
            }
            finally
            {
                if (filePtr != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(filePtr);
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }
    }

    // ------------------------------------------------------------------ Pacchetti MSI (msi.dll)

    public MsiReader.MsiInfo? ReadMsi(string path) => ReadMsiCore(path);

    private static MsiReader.MsiInfo? ReadMsiCore(string path)
    {
        if (MsiOpenDatabaseW(path, IntPtr.Zero, out var db) != 0)
            return null;
        try
        {
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (MsiDatabaseOpenViewW(db, "SELECT `Property`, `Value` FROM `Property`", out var view) == 0)
            {
                try
                {
                    if (MsiViewExecute(view, IntPtr.Zero) == 0)
                    {
                        while (MsiViewFetch(view, out var record) == 0)
                        {
                            try
                            {
                                var name = RecordString(record, 1);
                                var value = RecordString(record, 2);
                                if (!string.IsNullOrEmpty(name) && value is not null)
                                    properties.TryAdd(name, value);
                            }
                            finally
                            {
                                MsiCloseHandle(record);
                            }
                        }
                    }
                }
                finally
                {
                    MsiCloseHandle(view);
                }
            }
            return new MsiReader.MsiInfo(properties, ReadTemplate(path));
        }
        finally
        {
            MsiCloseHandle(db);
        }
    }

    /// <summary>Riepilogo "Template", es. "x64;1033": indica piattaforma e lingua del pacchetto.</summary>
    private static string? ReadTemplate(string path)
    {
        const uint PidTemplate = 7;
        if (MsiGetSummaryInformationW(IntPtr.Zero, path, 0, out var summary) != 0)
            return null;
        try
        {
            uint size = 0;
            var buffer = new StringBuilder(256);
            size = (uint)buffer.Capacity;
            return MsiSummaryInfoGetPropertyW(summary, PidTemplate, out _, out _, IntPtr.Zero, buffer, ref size) == 0
                ? buffer.ToString()
                : null;
        }
        finally
        {
            MsiCloseHandle(summary);
        }
    }

    private static string? RecordString(IntPtr record, uint field)
    {
        uint size = 0;
        var empty = new StringBuilder(0);
        const int ErrorMoreData = 234;
        var rc = MsiRecordGetStringW(record, field, empty, ref size);
        if (rc != ErrorMoreData && rc != 0)
            return null;
        var buffer = new StringBuilder((int)size + 1);
        size++;
        return MsiRecordGetStringW(record, field, buffer, ref size) == 0 ? buffer.ToString() : null;
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr handle);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiDatabaseOpenViewW(IntPtr database, string query, out IntPtr view);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewExecute(IntPtr view, IntPtr record);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiRecordGetStringW(IntPtr record, uint field, StringBuilder value, ref uint size);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetSummaryInformationW(IntPtr database, string path, uint updateCount, out IntPtr summary);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiSummaryInfoGetPropertyW(IntPtr summary, uint property, out uint dataType,
        out int intValue, IntPtr fileTime, StringBuilder value, ref uint size);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiCloseHandle(IntPtr handle);

    // ------------------------------------------------------------------ Unità di rete, volumi, priorità

    public string? MappedDriveToUnc(string drive)
    {
        var buffer = new StringBuilder(512);
        var length = buffer.Capacity;
        return WNetGetConnection(drive, buffer, ref length) == 0 && buffer.Length > 0 ? buffer.ToString() : null;
    }

    public uint? VolumeSerial(string root) =>
        GetVolumeInformation(root, null, 0, out var serial, out _, out _, null, 0) ? serial : null;

    public PcOrg.Core.Indexing.Ntfs.MftSnapshot? ReadVolumeSnapshot(string path, CancellationToken cancellationToken) =>
        MftVolumeReader.TryRead(path, cancellationToken);

    public bool IsElevated
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    public ThumbnailImage? GetThumbnail(string path, int size) => ShellThumbnail.Get(path, size);

    public (long Read, long Written)? ProcessIoCounters() =>
        GetProcessIoCounters(GetCurrentProcess(), out var io) ? ((long)io.ReadTransferCount, (long)io.WriteTransferCount) : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public IDisposable? EnterBackgroundMode() =>
        SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin) ? new BackgroundMode() : null;

    private sealed class BackgroundMode : IDisposable
    {
        public void Dispose() => SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
    }

    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetGetConnectionW")]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string rootPathName, StringBuilder? volumeName, int volumeNameSize,
        out uint serialNumber, out uint maxComponentLength, out uint fileSystemFlags, StringBuilder? fileSystemName, int fileSystemNameSize);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();
}
