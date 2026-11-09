using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        public bool Confirm(string message, string title) => false;
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
        var temp = Path.Combine(Path.GetTempPath(), "pcorg-ui-" + Guid.NewGuid().ToString("N"));
        var folder = Directory.CreateDirectory(Path.Combine(temp, "Download")).FullName;
        // Alcuni eseguibili veri, così le colonne di versione/firma e i prodotti hanno dati.
        File.Copy(Environment.ProcessPath!, Path.Combine(folder, "host-1.exe"));
        File.Copy(Environment.ProcessPath!, Path.Combine(folder, "host-2.exe"));
        File.WriteAllText(Path.Combine(folder, "nota.txt"), "x");

        var index = FileIndex.Open(Path.Combine(temp, "indice.db"));
        var root = index.AddRoot(folder);
        index.SyncRoot(root.Id, PcOrg.Core.Indexing.FolderScanner.Enumerate(root.Path));
        PcOrg.Core.Metadata.MetadataUpdater.Run(index);

        var app = new App();
        app.InitializeComponent(); // carica App.xaml: tema e risorse
        var window = new MainWindow { DataContext = new MainViewModel(index, new SilentDialogs()) };
        app.MainWindow = window;
        window.Show();
        Pump(TimeSpan.FromSeconds(3));

        var vm = (MainViewModel)window.DataContext;
        Assert.Equal(3, vm.Results.Count);
        Assert.Contains(vm.Products, p => p.Files.Count == 2);

        // Seleziona un file (pannello dettagli) e passa alla scheda prodotti.
        vm.SelectedFiles = [vm.Results.First(r => r.Name == "host-1.exe")];
        window.Tabs.SelectedIndex = 1;
        vm.SelectedProduct = vm.Products.First(p => p.Files.Count == 2);
        Pump(TimeSpan.FromSeconds(1));
        Assert.Equal(2, vm.ProductFiles.Count);

        var plan = MovePlanner.Plan([Path.Combine(folder, "nota.txt")], temp);
        var preview = new MovePreviewWindow(plan, temp, destinationMonitored: false) { Owner = window };
        preview.Show();
        Pump(TimeSpan.FromMilliseconds(300));
        preview.Close();

        var history = new MoveHistoryWindow(index, new SilentDialogs(), () => { }) { Owner = window };
        history.Show();
        Pump(TimeSpan.FromMilliseconds(300));
        history.Close();

        window.Close();
        app.Shutdown();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
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
