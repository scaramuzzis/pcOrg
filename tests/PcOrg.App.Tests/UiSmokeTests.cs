using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcOrg.App.Infrastructure;
using PcOrg.App.ViewModels;
using PcOrg.App.Views;
using PcOrg.Core.Moving;
using PcOrg.Core.Storage;

namespace PcOrg.App.Tests;

/// <summary>
/// Apre davvero le finestre WPF (solo su Windows): intercetta errori nello XAML, nei binding
/// e nell'avvio che la sola compilazione non rileva.
/// </summary>
public class UiSmokeTests
{
    private sealed class SilentDialogs : IDialogService
    {
        public string? PickFolder(string title) => null;
        public string? AskNetworkFolder() => null;
        public string? PickSaveFile(string title, string defaultName, string filter) => SavePath;
        public void ShowAbout() { }
        public void ShowHelp(string? chapter = null) { }
        public string? SavePath { get; set; }
        public bool Confirm(string message, string title, string confirmText = "Conferma") => ConfirmResult;
        public bool ConfirmResult { get; set; }
        public void Info(string message, string title) { }
        public void Error(string message, string title) => throw new InvalidOperationException(message);
        public MovePreviewResult? PreviewMove(IReadOnlyList<MoveItem> plan, string destination, bool destinationMonitored, string? summary = null) => PreviewResult;
        public MovePreviewResult? PreviewResult { get; set; }
        public void ShowMoveHistory(FileIndex index, Action onChanged) { }
    }

    [Fact]
    public void Le_finestre_si_aprono_e_mostrano_i_dati()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new Xunit.Sdk.XunitException("Errore nell'interfaccia: " + failure);
    }

    private static void Run()
    {
        // Nell'app vera il contesto è impostato da Application.Run: senza, i task asincroni
        // della finestra tornerebbero su un altro thread.
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        var temp = Path.Combine(Path.GetTempPath(), "pcorg-ui-" + Guid.NewGuid().ToString("N"));
        var folder = Directory.CreateDirectory(Path.Combine(temp, "Download")).FullName;
        // Alcuni eseguibili veri, così le colonne di versione/firma e i prodotti hanno dati.
        File.Copy(Environment.ProcessPath!, Path.Combine(folder, "host-1.exe"));
        File.Copy(Environment.ProcessPath!, Path.Combine(folder, "host-2.exe"));
        File.WriteAllText(Path.Combine(folder, "nota.txt"), "x");
        File.WriteAllText(Path.Combine(folder, "backup.gho"), "immagine ghost di prova"); // contenuto diverso da nota.txt: non sono cloni
        WritePng(Path.Combine(folder, "foto.png"), 1200, 800);
        Directory.CreateDirectory(Path.Combine(folder, "VecchiaStampante"));
        File.WriteAllText(Path.Combine(folder, "VecchiaStampante", "stampante.inf"),
            "[Version]\nClass=Printer\nProvider=Stampanti Spa\nDriverVer=01/02/2020,1.2.3.4\n[Manufacturer]\nX=Modelli\n[Modelli]\nStampante = Inst, USBPRINT\\VECCHIA_STAMPANTE_1234\n");

        var index = FileIndex.Open(Path.Combine(temp, "indice.db"));
        var root = index.AddRoot(folder);
        index.SyncRoot(root.Id, PcOrg.Core.Indexing.FolderScanner.Enumerate(root.Path));
        PcOrg.Core.Metadata.MetadataUpdater.Run(index);

        var app = new App();
        app.InitializeComponent(); // carica App.xaml: tema e risorse
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown; // il test apre e chiude più finestre principali
        var dialogs = new SilentDialogs { SavePath = Path.Combine(temp, "elenco.csv") };
        var window = new MainWindow { DataContext = new MainViewModel(index, dialogs) };
        app.MainWindow = window;
        window.Show();
        Pump(TimeSpan.FromSeconds(3));

        var vm = (MainViewModel)window.DataContext;
        Assert.Equal(6, vm.Results.Count);

        // Panoramica: si apre per prima, con i quattro riquadri e le barre per categoria
        Assert.Equal(MainViewModel.OverviewTab, window.Tabs.SelectedIndex);
        Assert.Equal(4, vm.DashboardCards.Count);
        Assert.Equal("6", vm.DashboardCards[0].Value);
        Assert.NotEmpty(vm.CategoryBars);
        vm.OpenCategoryCommand.Execute(vm.CategoryBars.First(b => b.Category == PcOrg.Core.Classification.FileCategory.Testo));
        Pump(TimeSpan.FromMilliseconds(500));
        Assert.Equal(MainViewModel.FilesTab, window.Tabs.SelectedIndex);
        Assert.Equal("nota.txt", Assert.Single(vm.Results).Name);
        vm.SelectedCategory = vm.Categories[0];
        Pump(TimeSpan.FromMilliseconds(500));

        // Cambiando scheda la selezione non resta quella della scheda precedente
        window.ResultsGrid.SelectedItem = vm.Results.First(r => r.Name == "nota.txt");
        Assert.Single(vm.SelectedFiles);
        window.Tabs.SelectedIndex = MainViewModel.DriversTab;
        Assert.Empty(vm.SelectedFiles);
        window.Tabs.SelectedIndex = MainViewModel.FilesTab;
        Assert.Single(vm.SelectedFiles);
        window.ResultsGrid.SelectedItem = null;

        // Anteprima: compare per le immagini, sparisce per gli altri file.
        vm.SelectedFiles = [vm.Results.First(r => r.Name == "foto.png")];
        Pump(TimeSpan.FromSeconds(1));
        Assert.True(vm.IsPreviewVisible);
        Assert.NotNull(vm.PreviewImage);
        Assert.StartsWith("1200 × 800", vm.PreviewInfo);
        vm.SelectedFiles = [vm.Results.First(r => r.Name == "nota.txt")];
        Assert.False(vm.IsPreviewVisible);
        var ghost = Assert.Single(vm.Categories, c => c.IsSubcategory && c.Name.StartsWith("Ghost"));
        vm.SelectedCategory = ghost;
        Pump(TimeSpan.FromMilliseconds(500));
        Assert.Equal("backup.gho", Assert.Single(vm.Results).Name);
        vm.SelectedCategory = vm.Categories[0];
        Pump(TimeSpan.FromMilliseconds(500));
        Assert.Contains(vm.Products, p => p.Files.Count == 2);

        // Seleziona un file (pannello dettagli) e passa alla scheda prodotti.
        vm.SelectedFiles = [vm.Results.First(r => r.Name == "host-1.exe")];
        window.Tabs.SelectedIndex = 2;
        vm.SelectedProduct = vm.Products.First(p => p.Files.Count == 2);
        Pump(TimeSpan.FromSeconds(1));
        Assert.Equal(2, vm.ProductFiles.Count);

        // Menu: scheda, pannelli, tema, esportazione
        vm.ShowTabCommand.Execute("1");
        Pump(TimeSpan.FromMilliseconds(300));
        Assert.Equal(MainViewModel.FilesTab, window.Tabs.SelectedIndex);
        vm.ShowPreviewPanel = false;
        Assert.False(vm.PreviewPanelVisible);
        vm.ShowPreviewPanel = true;
        vm.SetThemeCommand.Execute("Scuro");
        Pump(TimeSpan.FromMilliseconds(500));
        Assert.True(vm.IsThemeDark);
        vm.SetThemeCommand.Execute("Sistema");
        Pump(TimeSpan.FromMilliseconds(300));
        vm.ExportCsvCommand.Execute(null);
        Pump(TimeSpan.FromSeconds(1));
        Assert.True(File.Exists(dialogs.SavePath));
        Assert.True(File.ReadAllLines(dialogs.SavePath!).Length > 1);

        var about = new AboutWindow { Owner = window };
        about.Show();
        Pump(TimeSpan.FromMilliseconds(300));
        about.Close();

        // Guida: testo incorporato, capitoli e salto a un capitolo
        var help = new HelpWindow("Consigli") { Owner = window };
        help.Show();
        Pump(TimeSpan.FromMilliseconds(600));
        Assert.True(help.Chapters.Count >= 15, $"Capitoli trovati: {help.Chapters.Count}");
        Assert.Contains(help.Chapters, c => c.Title.StartsWith("Cartelle di rete"));
        Assert.Equal("Consigli per organizzare al meglio", ((PcOrg.App.Infrastructure.HelpChapter)help.ChapterList.SelectedItem).Title);
        help.Close();

        var plan = MovePlanner.Plan([Path.Combine(folder, "nota.txt")], temp);
        var preview = new MovePreviewWindow(plan, temp, destinationMonitored: false) { Owner = window };
        preview.Show();
        Pump(TimeSpan.FromMilliseconds(300));
        preview.Close();

        // Notifica a comparsa con azione
        var undone = false;
        vm.ShowToast("3 file spostati", ToastKind.Success, "Annulla", () => { undone = true; return Task.CompletedTask; });
        Pump(TimeSpan.FromMilliseconds(300));
        Assert.True(vm.IsToastVisible);
        vm.ToastActionCommand.Execute(null);
        Pump(TimeSpan.FromMilliseconds(200));
        Assert.True(undone);
        Assert.False(vm.IsToastVisible);

        // Finestra di dialogo moderna
        var dialog = new ModernDialog("Titolo", "Messaggio di prova", DialogKind.Warning, "OK", "Annulla") { Owner = window };
        dialog.Show();
        Pump(TimeSpan.FromMilliseconds(300));
        dialog.Close();

        var history = new MoveHistoryWindow(index, new SilentDialogs(), () => { }) { Owner = window };
        history.Show();
        Pump(TimeSpan.FromMilliseconds(300));
        history.Close();

        // Cloni: host-1.exe e host-2.exe sono copie identiche
        window.Tabs.SelectedIndex = 3;
        vm.SelectedSizeOption = vm.SizeOptions[0];
        await_(vm.FindDuplicatesCommand.ExecuteAsync(null));
        Assert.Single(vm.DuplicateGroups);
        vm.MarkDoublesCommand.Execute(null);
        Assert.Equal(1, vm.MarkedCount);
        var originalRecycle = PcOrg.Core.Moving.RecycleBin.SendToRecycleBin;
        PcOrg.Core.Moving.RecycleBin.SendToRecycleBin = path => { File.Delete(path); return true; };
        try
        {
            dialogs.ConfirmResult = true;
            await_(vm.RecycleMarkedCommand.ExecuteAsync(null));
            dialogs.ConfirmResult = false;
            Assert.Empty(vm.DuplicateGroups);
            Assert.Single(Directory.GetFiles(folder, "host-*.exe"));
        }
        finally
        {
            PcOrg.Core.Moving.RecycleBin.SendToRecycleBin = originalRecycle;
        }
        window.Tabs.SelectedIndex = 1;

        // Regole di archiviazione: i file di testo vanno in Archivio\Testi\{Anno}
        vm.SetRules([new PcOrg.Core.Rules.ArchiveRule
        {
            Name = "Testi", Category = PcOrg.Core.Classification.FileCategory.Testo,
            Destination = Path.Combine(temp, "Archivio", "Testi", "{Anno}"),
        }]);
        vm.SelectedFiles = [];
        dialogs.PreviewResult = new MovePreviewResult(AddDestinationToMonitored: true);
        await_(vm.OrganizeWithRulesCommand.ExecuteAsync(null));
        dialogs.PreviewResult = null;
        var archived = Directory.GetFiles(Path.Combine(temp, "Archivio", "Testi"), "nota.txt", SearchOption.AllDirectories);
        Assert.Single(archived);
        Assert.Contains(vm.Roots, r => r.Path.EndsWith("Testi")); // la cartella base della regola ora è monitorata

        // Colonna "Sul PC" e dettagli: il confronto con i programmi installati non deve fallire
        await_(vm.RefreshInstalledAsync());

        // Driver e periferiche: il driver della stampante che non c'è va fra quelli che non servono
        window.Tabs.SelectedIndex = 4;
        Pump(TimeSpan.FromMilliseconds(400));
        Assert.Contains("periferiche presenti", vm.DriversSummary);
        vm.SelectUnneededDriversCommand.Execute(null);
        Assert.Contains(vm.SelectedFiles, f => f.Name == "stampante.inf");
        Assert.Contains(vm.DriverFiles, d => d.State == PcOrg.Core.Drivers.DriverState.NonServe);
        vm.ShowUnneededDrivers = false;
        window.Tabs.SelectedIndex = 1;
        window.ResultsGrid.Columns.First(c => (string)c.Header == "Sul PC").Visibility = Visibility.Visible;
        Pump(TimeSpan.FromMilliseconds(300));

        // Impostazioni e colonne: si nasconde "Versione", si sposta "Cartella" in seconda posizione,
        // si chiude e si riapre la finestra con le stesse preferenze.
        var settingsPath = Path.Combine(temp, "settings.json");
        var settings = AppSettings.Load(settingsPath);
        settings.ScanOnStartup = false;
        vm.UseSettings(settings);
        var version = window.ResultsGrid.Columns.First(c => (string)c.Header == "Versione");
        var folder2 = window.ResultsGrid.Columns.First(c => (string)c.Header == "Cartella");
        version.Visibility = Visibility.Collapsed;
        folder2.DisplayIndex = 1;
        window.ResultsGrid.Columns.First(c => (string)c.Header == "Percorso completo").Visibility = Visibility.Visible;
        var settingsWindow = new SettingsWindow(vm, window, new Dictionary<string, DataGrid> { ["file"] = window.ResultsGrid }, SettingsWindow.ColumnsTab) { Owner = window };
        settingsWindow.Show();
        Pump(TimeSpan.FromMilliseconds(400));
        settingsWindow.Close();
        window.SaveLayout();
        window.Close();
        Pump(TimeSpan.FromMilliseconds(200));

        // "Riavvio": nuovo indice dallo stesso file, nuove preferenze dallo stesso file
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var reopenedIndex = FileIndex.Open(Path.Combine(temp, "indice.db"));
        var vm2 = new MainViewModel(reopenedIndex, new SilentDialogs());
        vm2.UseSettings(AppSettings.Load(settingsPath));
        var window2 = new MainWindow { DataContext = vm2 };
        window2.Show();
        Pump(TimeSpan.FromSeconds(2));
        Assert.Equal(vm.Results.Count, vm2.Results.Count);
        Assert.True(vm2.Results.Count > 0, "Dopo il riavvio l'elenco dei file deve ricomparire");
        var restoredVersion = window2.ResultsGrid.Columns.First(c => (string)c.Header == "Versione");
        var restoredFolder = window2.ResultsGrid.Columns.First(c => (string)c.Header == "Cartella");
        Assert.Equal(Visibility.Collapsed, restoredVersion.Visibility);
        Assert.Equal(1, restoredFolder.DisplayIndex);
        Assert.Equal(Visibility.Visible, window2.ResultsGrid.Columns.First(c => (string)c.Header == "Percorso completo").Visibility);
        window2.Close();

        // Schermata di benvenuto con un indice vuoto
        var empty = FileIndex.Open(Path.Combine(temp, "vuoto.db"));
        var welcome = new MainWindow { DataContext = new MainViewModel(empty, new SilentDialogs()) };
        welcome.Show();
        Pump(TimeSpan.FromSeconds(1));
        Assert.False(((MainViewModel)welcome.DataContext).HasRoots);
        welcome.Close();

        app.Shutdown();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
    }

    private static void WritePng(string path, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200; pixels[i + 1] = 120; pixels[i + 2] = 40; pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Attende un'operazione asincrona continuando a elaborare i messaggi della finestra.</summary>
    private static void await_(Task task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    /// <summary>Elabora i messaggi della finestra per il tempo indicato (binding, rendering, task asincroni).</summary>
    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
