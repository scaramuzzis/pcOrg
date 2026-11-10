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
        public bool Confirm(string message, string title, string confirmText = "Conferma") => false;
        public void Info(string message, string title) { }
        public void Error(string message, string title) => throw new InvalidOperationException(message);
        public MovePreviewResult? PreviewMove(IReadOnlyList<MoveItem> plan, string destination, bool destinationMonitored) => null;
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
        File.WriteAllText(Path.Combine(folder, "backup.gho"), "x");
        WritePng(Path.Combine(folder, "foto.png"), 1200, 800);

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
        Assert.Equal(5, vm.Results.Count);

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
        window.Tabs.SelectedIndex = 1;
        vm.SelectedProduct = vm.Products.First(p => p.Files.Count == 2);
        Pump(TimeSpan.FromSeconds(1));
        Assert.Equal(2, vm.ProductFiles.Count);

        // Menu: scheda, pannelli, tema, esportazione
        vm.ShowTabCommand.Execute("0");
        Pump(TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, window.Tabs.SelectedIndex);
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

        window.Close();

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
