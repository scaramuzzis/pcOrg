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

    /// <summary>
    /// Avvio. Regola fondamentale: <b>una finestra compare subito</b> e nulla di lento gira sul thread
    /// dell'interfaccia. L'indice (che dopo un aggiornamento può dover essere convertito, anche per
    /// minuti) viene aperto in background mentre è visibile la finestra di avvio.
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Finché non c'è la finestra principale, chiudere quella di avvio non deve terminare il programma.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Registro attività: tutto ciò che succede (e ogni errore) finisce in log\pcorg.log.
        Log.Initialize(AppPaths.LogFolder);

        // Una sola istanza per cartella dei dati: due copie aperte insieme scriverebbero sullo stesso
        // indice e sulle stesse preferenze. Se Gestione File è già aperto si porta in primo piano quello.
        _instance = new Mutex(true, SingleInstance.MutexName(AppPaths.DataFolder), out var first);
        if (!first)
        {
            Log.Info("Avvio ignorato: Gestione File è già in esecuzione.");
            if (!SingleInstance.ActivateRunning())
                MessageBox.Show("Gestione File è già in esecuzione e sta ancora preparando i dati.\n\n" +
                                "Attendi che compaia la sua finestra. Se non compare dopo alcuni minuti, chiudi il processo " +
                                "\"GestioneFile\" da Gestione attività e riavvia.", "Gestione File", MessageBoxButton.OK, MessageBoxImage.Information);
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

        Log.Info($"=== Avvio di Gestione File {AboutWindow.AppVersion} — dati in {AppPaths.DataFolder} ===");

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Errore non gestito: il programma verrà chiuso.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Errore in un'operazione in background.", args.Exception);
            args.SetObserved();
        };

        var splash = new StartupWindow();
        splash.Show();
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var index = await Task.Run(() => FileIndex.Open(AppPaths.DatabaseFile));
            Log.Info($"Indice aperto in {stopwatch.Elapsed.TotalSeconds:N1} s.");

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
            // Memoria: finita un'operazione pesante, o quando la finestra viene ridotta a icona, si
            // restituisce al sistema ciò che non serve più.
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.IsBusy) && !viewModel.IsBusy)
                    _ = Task.Run(MemoryTrim.Run);
            };
            var window = new MainWindow { DataContext = viewModel };
            window.StateChanged += (_, _) =>
            {
                if (window.WindowState == WindowState.Minimized)
                    _ = Task.Run(MemoryTrim.Run);
            };
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            splash.Close();
            Log.Info($"Finestra principale mostrata dopo {stopwatch.Elapsed.TotalSeconds:N1} s.");
        }
        catch (Exception ex)
        {
            // Qualsiasi errore di avvio viene mostrato: il programma non deve mai restare aperto senza finestre.
            Log.Error("Avvio non riuscito.", ex);
            splash.Close();
            MessageBox.Show($"Gestione File non è riuscito ad avviarsi.\n\n{ex.Message}\n\n" +
                            $"Archivio dati: {AppPaths.DatabaseFile}\nIl dettaglio è nel registro: {Log.FilePath}",
                "Gestione File", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_sessionStarted)
        {
            SessionGuard.End();
            Log.Info("=== Chiusura di Gestione File ===");
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
