using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PcOrg.App.Infrastructure;

/// <summary>Garantisce una sola istanza di pcOrg per cartella dei dati e porta in primo piano quella già aperta.</summary>
public static class SingleInstance
{
    /// <summary>Nome del mutex: dipende dalla cartella dei dati, così copie portatili diverse possono convivere.</summary>
    public static string MutexName(string dataFolder)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(dataFolder.ToUpperInvariant()));
        return "Local\\pcOrg-" + Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>Porta in primo piano la finestra dell'istanza già in esecuzione.</summary>
    /// <returns>Falso se l'altra istanza non ha (ancora) una finestra, es. sta preparando i dati.</returns>
    public static bool ActivateRunning()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                using (process)
                {
                    if (process.Id == current.Id || process.MainWindowHandle == IntPtr.Zero)
                        continue;
                    if (IsIconic(process.MainWindowHandle))
                        ShowWindow(process.MainWindowHandle, 9 /* SW_RESTORE */);
                    SetForegroundWindow(process.MainWindowHandle);
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Non è stato possibile attivarla: l'utente la troverà nella barra delle applicazioni.
        }
        return false;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
}
