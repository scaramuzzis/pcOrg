using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PcOrg.Core.Metadata;

/// <summary>Legge le proprietà di un pacchetto Windows Installer (.msi) tramite msi.dll.</summary>
[SupportedOSPlatform("windows")]
public static class MsiReader
{
    /// <summary>Proprietà del pacchetto (tabella Property) e riepilogo "Template" (piattaforma;lingua).</summary>
    public sealed record MsiInfo(IReadOnlyDictionary<string, string> Properties, string? Template);

    public static MsiInfo? Read(string path)
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
            return new MsiInfo(properties, ReadTemplate(path));
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

    public static Architecture ArchitectureFromTemplate(string? template)
    {
        var platform = template?.Split(';')[0].Trim().ToLowerInvariant();
        return platform switch
        {
            "x64" or "amd64" or "intel64" => Architecture.X64,
            "arm64" => Architecture.Arm64,
            "arm" => Architecture.Arm,
            "intel" or "" => Architecture.X86,
            _ => Architecture.Sconosciuta,
        };
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
}
