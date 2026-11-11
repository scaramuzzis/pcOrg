using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using PcOrg.App.Infrastructure;
using PcOrg.App.ViewModels;
using PcOrg.App.Views;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Storage;

namespace PcOrg.App;

/// <summary>
/// Punto di ingresso dell'applicazione: imposta lingua e registro attività, apre l'indice,
/// carica le preferenze e mostra la finestra principale.
/// </summary>
public partial class App : Application
{
    private Mutex? _instance;
    private bool _sessionStarted;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Una sola istanza per cartella dei dati: due copie aperte insieme scriverebbero sullo stesso
        // indice e sulle stesse preferenze. Se pcOrg è già aperto si porta in primo piano quello.
        _instance = new Mutex(true, SingleInstance.MutexName(AppPaths.DataFolder), out var first);
        if (!first)
        {
            SingleInstance.ActivateRunning();
            Shutdown(0);
            return;
        }
        // Se Windows si spegne o l'utente si disconnette, la sessione si considera chiusa regolarmente.
        SessionEnding += (_, _) => SessionGuard.End();

        // Interfaccia solo in italiano: date e numeri in formato italiano anche nei binding.
        var culture = CultureInfo.GetCultureInfo("it-IT");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

        // Registro attività: tutto ciò che succede (e ogni errore) finisce in log\pcorg.log.
        Log.Initialize(AppPaths.LogFolder);
        Log.Info($"=== Avvio di pcOrg {AboutWindow.AppVersion} — dati in {AppPaths.DataFolder} ===");

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Errore non gestito: il programma verrà chiuso.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Errore in un'operazione in background.", args.Exception);
            args.SetObserved();
        };

        FileIndex index;
        try
        {
            index = FileIndex.Open(AppPaths.DatabaseFile);
        }
        catch (Exception ex)
        {
            Log.Error($"Impossibile aprire l'indice {AppPaths.DatabaseFile}.", ex);
            MessageBox.Show($"Impossibile aprire l'archivio dati:\n{AppPaths.DatabaseFile}\n\n{ex.Message}",
                "pcOrg", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Modalità sicura: se l'ultima sessione non si è chiusa correttamente (blocco o chiusura forzata),
        // questa volta si saltano le operazioni automatiche pesanti, così l'app si apre comunque.
        var safeMode = SessionGuard.PreviousSessionCrashed();
        SessionGuard.Begin();
        _sessionStarted = true;

        var viewModel = new MainViewModel(index, new DialogService());
        viewModel.UseSettings(AppSettings.Load());
        if (safeMode)
        {
            Log.Warn("La sessione precedente non si è chiusa correttamente: avvio in modalità sicura.");
            viewModel.EnterSafeMode();
        }
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_sessionStarted)
        {
            SessionGuard.End();
            Log.Info("=== Chiusura di pcOrg ===");
        }
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Errore imprevisto nell'interfaccia: lo si registra e lo si mostra senza chiudere il programma.</summary>
    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Errore imprevisto nell'interfaccia.", e.Exception);
        ModernDialog.Show(Current?.MainWindow, "Si è verificato un errore imprevisto",
            $"{e.Exception.Message}\n\nIl dettaglio è stato salvato nel registro attività (Strumenti → Apri il registro attività).",
            DialogKind.Error);
        e.Handled = true;
    }
}
