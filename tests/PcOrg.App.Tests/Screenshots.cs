using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcOrg.App.Infrastructure;
using PcOrg.App.ViewModels;
using PcOrg.App.Views;
using PcOrg.Core.Indexing;
using PcOrg.Core.Metadata;
using PcOrg.Core.Moving;
using PcOrg.Core.Storage;

namespace PcOrg.App.Tests;

/// <summary>
/// Genera gli screenshot dell'applicazione con dati di esempio. Gira solo se la variabile
/// d'ambiente PCORG_SCREENSHOTS indica la cartella di destinazione (lo fa la build su GitHub).
/// Va eseguito in un processo separato dagli altri test (una sola Application per processo).
/// </summary>
public class Screenshots
{
    private sealed class SilentDialogs : IDialogService
    {
        public string? PickFolder(string title) => null;
        public bool Confirm(string message, string title, string confirmText = "Conferma") => false;
        public void Info(string message, string title) { }
        public void Error(string message, string title) { }
        public MovePreviewResult? PreviewMove(IReadOnlyList<MoveItem> plan, string destination, bool destinationMonitored) => null;
        public void ShowMoveHistory(FileIndex index, Action onChanged) { }
    }

    [Fact]
    public void Genera_screenshot()
    {
        var output = Environment.GetEnvironmentVariable("PCORG_SCREENSHOTS");
        if (string.IsNullOrEmpty(output) || !OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(output);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Run(output); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new Xunit.Sdk.XunitException("Errore negli screenshot: " + failure);
    }

    private static void Run(string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var temp = Path.Combine(Path.GetTempPath(), "pcorg-shots-" + Guid.NewGuid().ToString("N"));
        var download = Path.Combine(temp, "Download");
        var archive = Path.Combine(temp, "Archivio");
        SampleData.Create(download);
        Directory.CreateDirectory(archive);

        var index = FileIndex.Open(Path.Combine(temp, "indice.db"));
        var root = index.AddRoot(download);
        index.SyncRoot(root.Id, FolderScanner.Enumerate(root.Path));
        index.AddRoot(archive);
        MetadataUpdater.Run(index);

        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 1. Benvenuto (indice vuoto)
        var welcome = new MainWindow { DataContext = new MainViewModel(FileIndex.Open(Path.Combine(temp, "vuoto.db")), new SilentDialogs()) };
        Show(welcome);
        Capture(welcome, output, "01-benvenuto.png");
        welcome.Close();

        // 2. Elenco file con dettagli di un programma firmato
        var vm = new MainViewModel(index, new SilentDialogs());
        var window = new MainWindow { DataContext = vm };
        app.MainWindow = window;
        Show(window);
        var exe = vm.Results.Where(r => r.Meta?.Signer is not null).OrderByDescending(r => r.Name.StartsWith("python")).FirstOrDefault()
                  ?? vm.Results.First();
        Select(window, vm, exe);
        Capture(window, output, "02-elenco-file.png");

        // 3. Anteprima di un'immagine
        vm.SelectedCategory = vm.Categories.First(c => c.Name == "Immagini");
        Pump(600);
        Select(window, vm, vm.Results.First());
        Pump(800);
        Capture(window, output, "03-anteprima-immagine.png");

        // 4. Immagini disco → Ghost
        vm.SelectedCategory = vm.Categories.First(c => c.IsSubcategory && c.Name.StartsWith("Ghost"));
        Pump(600);
        Select(window, vm, vm.Results.First());
        Capture(window, output, "04-immagini-ghost.png");

        // 5. Ricerca
        vm.SelectedCategory = vm.Categories[0];
        vm.SearchText = "driver";
        Pump(900);
        if (vm.Results.Count > 0)
            Select(window, vm, vm.Results.First());
        Capture(window, output, "05-ricerca.png");
        vm.SearchText = "";
        Pump(600);

        // 6. Prodotti e versioni
        window.Tabs.SelectedIndex = 1;
        vm.SelectedProduct = vm.Products.OrderByDescending(p => p.Files.Count).FirstOrDefault();
        Pump(800);
        Capture(window, output, "06-prodotti-e-versioni.png");

        // 7. Anteprima dello spostamento delle versioni vecchie
        var old = vm.SelectedProduct?.OldVersions.Select(f => f.Path).ToList() ?? [];
        if (old.Count == 0)
            old = vm.Results.Take(3).Select(f => f.Path).ToList();
        var plan = MovePlanner.Plan(old, Path.Combine(archive, "Programmi"));
        var preview = new MovePreviewWindow(plan, Path.Combine(archive, "Programmi"), destinationMonitored: true) { Owner = window };
        Show(preview);
        Capture(preview, output, "07-anteprima-spostamento.png");
        preview.Close();

        // 8. Notifica dopo lo spostamento (spostamento reale, così la cronologia ha dati)
        window.Tabs.SelectedIndex = 0;
        var moved = new MoveService(index).Execute(plan, $"{plan.Count(p => p.WillMove)} file in {Path.Combine(archive, "Programmi")}");
        vm.ShowToast($"{moved.Moved} file spostati in Programmi", ToastKind.Success, "Annulla", () => Task.CompletedTask);
        Pump(700);
        Capture(window, output, "08-notifica-con-annulla.png");
        vm.DismissToastCommand.Execute(null);

        // 9. Finestra di conferma
        var dialog = new ModernDialog("Eseguire il file?",
            "\"python-3.12.exe\" è un programma o un installer: verrà avviato.\n\nPercorso: C:\\Users\\Mario\\Download\\python-3.12.exe",
            DialogKind.Question, "Esegui", "Annulla") { Owner = window };
        Show(dialog);
        Capture(dialog, output, "09-conferma.png");
        dialog.Close();

        // 10. Cronologia spostamenti
        var history = new MoveHistoryWindow(index, new SilentDialogs(), () => { }) { Owner = window };
        Show(history);
        var batchGrid = (System.Windows.Controls.DataGrid)history.FindName("BatchGrid");
        if (batchGrid.Items.Count > 0)
            batchGrid.SelectedIndex = 0;
        Pump(500);
        Capture(history, output, "10-cronologia.png");
        history.Close();

        // 11. Tema scuro
        app.ThemeMode = ThemeMode.Dark;
        Pump(1000);
        Select(window, vm, exe);
        Pump(500);
        Capture(window, output, "11-tema-scuro.png");

        window.Close();
        app.Shutdown();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
    }

    private static void Show(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 0;
        window.Top = 0;
        if (window is MainWindow)
        {
            window.Width = 1280;
            window.Height = 800;
        }
        window.Show();
        window.Activate();
        Pump(1500);
    }

    private static void Select(MainWindow window, MainViewModel vm, FileRecord file)
    {
        var grid = window.ResultsGrid;
        grid.SelectedItem = vm.Results.FirstOrDefault(r => r.Id == file.Id) ?? file;
        if (grid.SelectedItem is not null)
            grid.ScrollIntoView(grid.SelectedItem);
        vm.SelectedFiles = [file];
        Pump(600);
    }

    // ------------------------------------------------------------------ Cattura

    private static void Capture(Window window, string folder, string name)
    {
        window.UpdateLayout();
        Pump(300);
        var shot = TryPrintWindow(window);
        if (shot is null || IsMostlyBlack(shot))
            shot = RenderContent(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(shot));
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
    }

    /// <summary>Copia della finestra come la disegna Windows (barra del titolo compresa).</summary>
    private static BitmapSource? TryPrintWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var frame, Marshal.SizeOf<NativeRect>()) != 0
            || !GetWindowRect(hwnd, out var outer))
            return null;

        var width = outer.Right - outer.Left;
        var height = outer.Bottom - outer.Top;
        if (width <= 0 || height <= 0)
            return null;

        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            if (!PrintWindow(hwnd, memory, 2 /* PW_RENDERFULLCONTENT */))
                return null;
            SelectObject(memory, previous);
            var full = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // Si tolgono i bordi invisibili usati per il ridimensionamento.
            var crop = new Int32Rect(frame.Left - outer.Left, frame.Top - outer.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
            BitmapSource result = crop.Width > 0 && crop.Height > 0 && crop.X >= 0 && crop.Y >= 0
                                  && crop.X + crop.Width <= full.PixelWidth && crop.Y + crop.Height <= full.PixelHeight
                ? new CroppedBitmap(full, crop)
                : full;
            result.Freeze();
            return result;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Ripiego: disegna il contenuto WPF della finestra su uno sfondo pieno.</summary>
    private static BitmapSource RenderContent(Window window)
    {
        var content = (FrameworkElement)window.Content;
        var width = Math.Max(1, (int)Math.Ceiling(content.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(content.ActualHeight));
        var dark = Application.Current.ThemeMode == ThemeMode.Dark;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, width, height);
            dc.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)), null, rect);
            dc.DrawRectangle(new VisualBrush(content), null, rect);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static bool IsMostlyBlack(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        long sum = 0, samples = 0;
        for (var i = 0; i < pixels.Length; i += 4 * 97)
        {
            sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
            samples++;
        }
        return samples == 0 || sum / (samples * 3.0) < 8;
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);
}

/// <summary>Cartella "Download" di esempio, realistica, per gli screenshot.</summary>
internal static class SampleData
{
    public static void Create(string root)
    {
        Directory.CreateDirectory(root);

        // Programmi veri presenti sulla macchina di build: versioni diverse dello stesso prodotto
        // (Python, Node.js) e programmi firmati da produttori diversi.
        var programs = Path.Combine(root, "Programmi");
        Directory.CreateDirectory(programs);
        foreach (var python in FindAll(@"C:\hostedtoolcache\windows\Python", "python.exe", 4))
            CopyAs(python, Path.Combine(programs, $"python-{VersionFromPath(python)}-amd64.exe"));
        foreach (var node in FindAll(@"C:\hostedtoolcache\windows\node", "node.exe", 3))
            CopyAs(node, Path.Combine(programs, $"node-v{VersionFromPath(node)}-x64.exe"));
        foreach (var (source, name) in new[]
                 {
                     (@"C:\Program Files\7-Zip\7zFM.exe", "7z2408-x64.exe"),
                     (@"C:\Program Files\Git\cmd\git.exe", "Git-2.47.1-64-bit.exe"),
                     (@"C:\Program Files\PowerShell\7\pwsh.exe", "PowerShell-7.4.6-win-x64.exe"),
                     (@"C:\Program Files\Mozilla Firefox\firefox.exe", "Firefox Setup 132.0.exe"),
                     (@"C:\Program Files\Google\Chrome\Application\chrome.exe", "ChromeSetup.exe"),
                 })
            CopyAs(source, Path.Combine(root, name));
        CopyAs(Environment.ProcessPath!, Path.Combine(root, "dotnet-runtime-10.0.0-win-x64.exe"));

        // Driver
        Fake(Path.Combine(root, "Driver", "NVIDIA", "566.36-desktop-win10-win11-64bit-international-dch-whql.exe"), 812_000);
        Fake(Path.Combine(root, "Driver", "Realtek", "RtHDA.inf"), 48_000);
        Fake(Path.Combine(root, "Driver", "Realtek", "RTKVHD64.sys"), 6_200_000);
        Fake(Path.Combine(root, "intel-wifi-driver-23.80.zip"), 54_000_000);

        // Documenti
        Fake(Path.Combine(root, "Manuale scheda madre ASUS B550.pdf"), 7_400_000);
        Fake(Path.Combine(root, "Fattura 2024-118.pdf"), 120_000);
        Fake(Path.Combine(root, "Contratto fornitura.docx"), 86_000);
        Fake(Path.Combine(root, "Lettera di presentazione.doc"), 44_000);
        Fake(Path.Combine(root, "Bilancio 2024.xlsx"), 230_000);
        Fake(Path.Combine(root, "Presentazione progetto.pptx"), 3_100_000);
        Fake(Path.Combine(root, "leggimi.txt"), 3_000);
        Fake(Path.Combine(root, "licenze software.txt"), 1_500);

        // Archivi e immagini disco
        Fake(Path.Combine(root, "Backup", "PC-Ufficio-2024.gho"), 2_000_000);
        Fake(Path.Combine(root, "Backup", "PC-Ufficio-2024001.ghs"), 2_000_000);
        Fake(Path.Combine(root, "Backup", "PC-Ufficio-2024002.ghs"), 1_400_000);
        Fake(Path.Combine(root, "Backup", "Portatile-Casa.gho"), 1_800_000);
        Fake(Path.Combine(root, "Win11_24H2_Italian_x64.iso"), 5_800_000);
        Fake(Path.Combine(root, "ubuntu-24.04.1-desktop-amd64.iso"), 5_900_000);
        Fake(Path.Combine(root, "VM", "Windows10-Test.vhdx"), 4_000_000);
        Fake(Path.Combine(root, "Backup", "Sistema-completo.tibx"), 3_000_000);
        Fake(Path.Combine(root, "foto-vecchie.zip"), 1_200_000);
        Fake(Path.Combine(root, "progetti.7z"), 900_000);

        // Immagini (vere, per l'anteprima)
        var photos = Path.Combine(root, "Foto");
        Directory.CreateDirectory(photos);
        var palettes = new[]
        {
            (Color.FromRgb(14, 116, 144), Color.FromRgb(251, 191, 36)),
            (Color.FromRgb(30, 64, 175), Color.FromRgb(236, 72, 153)),
            (Color.FromRgb(22, 101, 52), Color.FromRgb(190, 242, 100)),
            (Color.FromRgb(124, 45, 18), Color.FromRgb(253, 186, 116)),
        };
        var names = new[] { "Vacanze Sardegna 2024.jpg", "Tramonto al lago.jpg", "Montagna - Dolomiti.png", "Cena di Natale.jpg" };
        for (var i = 0; i < names.Length; i++)
            Picture(Path.Combine(photos, names[i]), 1600, 1067, palettes[i].Item1, palettes[i].Item2);
    }

    private static IEnumerable<string> FindAll(string folder, string exe, int max)
    {
        if (!Directory.Exists(folder))
            return [];
        return Directory.EnumerateDirectories(folder)
            .Select(v => Path.Combine(v, "x64", exe))
            .Where(File.Exists)
            .OrderBy(p => p)
            .Take(max)
            .ToList();
    }

    private static string VersionFromPath(string path) =>
        Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))!) ?? "x";

    private static void CopyAs(string source, string target)
    {
        if (!File.Exists(source))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }

    private static void Fake(string path, long size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.SetLength(size); // file sparso: occupa poco spazio reale
    }

    private static void Picture(string path, int width, int height, Color from, Color to)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(from, to, 35), null, new System.Windows.Rect(0, 0, width, height));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), null,
                new System.Windows.Point(width * 0.72, height * 0.32), height * 0.14, height * 0.14);
            var hills = new StreamGeometry();
            using (var g = hills.Open())
            {
                g.BeginFigure(new System.Windows.Point(0, height), true, true);
                g.LineTo(new System.Windows.Point(0, height * 0.7), true, false);
                g.BezierTo(new System.Windows.Point(width * 0.3, height * 0.45), new System.Windows.Point(width * 0.55, height * 0.85),
                    new System.Windows.Point(width, height * 0.6), true, false);
                g.LineTo(new System.Windows.Point(width, height), true, false);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(170, 15, 23, 42)), null, hills);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        BitmapEncoder encoder = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? new PngBitmapEncoder()
            : new JpegBitmapEncoder { QualityLevel = 88 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
