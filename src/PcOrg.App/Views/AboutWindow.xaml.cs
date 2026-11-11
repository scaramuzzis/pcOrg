using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using PcOrg.App.Infrastructure;

namespace PcOrg.App.Views;

/// <summary>Finestra "Informazioni su Gestione File": versione, autore, cartella dei dati.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Versione {AppVersion}";
        CopyrightText.Text = $"© {DateTime.Now.Year} Scaramus. Tutti i diritti riservati.";
        DataFolderText.Text = AppPaths.DataFolder;
        DataFolderText.ToolTip = AppPaths.DataFolder;
        RuntimeText.Text = $"{RuntimeInformation.FrameworkDescription}, WPF";
    }

    /// <summary>Versione dell'applicazione (es. "0.5.0"), senza il codice del commit.</summary>
    public static string AppVersion
    {
        get
        {
            var info = typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return info?.Split('+')[0] ?? typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        }
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataFolder}\"") { UseShellExecute = true });
}
