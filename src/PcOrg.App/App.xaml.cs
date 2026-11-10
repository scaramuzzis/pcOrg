using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using PcOrg.App.Infrastructure;
using PcOrg.App.ViewModels;
using PcOrg.App.Views;
using PcOrg.Core.Storage;

namespace PcOrg.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Interfaccia solo in italiano: date e numeri in formato italiano anche nei binding.
        var culture = CultureInfo.GetCultureInfo("it-IT");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

        DispatcherUnhandledException += OnUnhandledException;

        FileIndex index;
        try
        {
            index = FileIndex.Open(AppPaths.DatabaseFile);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossibile aprire l'archivio dati:\n{AppPaths.DatabaseFile}\n\n{ex.Message}",
                "pcOrg", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow { DataContext = new MainViewModel(index, new DialogService()) };
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Si è verificato un errore imprevisto:\n\n{e.Exception.Message}",
            "pcOrg", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
