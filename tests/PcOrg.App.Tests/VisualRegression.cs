using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PcOrg.App.Tests;

/// <summary>
/// Confronto automatico degli screenshot con le immagini di riferimento approvate
/// (tests/PcOrg.App.Tests/Riferimenti). Gira nella build su GitHub dopo la generazione degli screenshot:
/// se una schermata cambia più della tolleranza la build fallisce e l'artifact "confronto-screenshot"
/// contiene, per ogni differenza, un'immagine con i pixel cambiati evidenziati in rosso.
/// </summary>
public class VisualRegression
{
    /// <summary>Un pixel è "diverso" se un canale cambia più di così (assorbe l'antialiasing del testo).</summary>
    public const int ChannelTolerance = 48;

    /// <summary>Percentuale massima di pixel diversi: date e percorsi temporanei cambiano a ogni build.</summary>
    public const double MaxDifferentPercent = 0.5;

    /// <summary>Schermate che dipendono dal computer della build (dischi presenti) e non si confrontano.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { "34-dischi.png" };

    public sealed record Result(string Name, double DifferentPercent, bool SizeChanged)
    {
        public bool Failed => SizeChanged || DifferentPercent > MaxDifferentPercent;
    }

    [Fact]
    public void Confronta_con_i_riferimenti()
    {
        var shots = Environment.GetEnvironmentVariable("PCORG_SCREENSHOTS");
        var references = Environment.GetEnvironmentVariable("PCORG_REFERENCES");
        if (string.IsNullOrEmpty(shots) || string.IsNullOrEmpty(references) || !OperatingSystem.IsWindows()
            || !Directory.Exists(shots) || !Directory.Exists(references))
            return;

        var report = Path.Combine(shots, "..", "confronto-screenshot");
        Directory.CreateDirectory(report);
        var results = new List<Result>();
        foreach (var reference in Directory.EnumerateFiles(references, "*.png").OrderBy(p => p))
        {
            var name = Path.GetFileName(reference);
            var current = Path.Combine(shots, name);
            if (Ignored.Contains(name) || !File.Exists(current))
                continue;
            var result = Compare(name, Load(reference), Load(current), Path.Combine(report, Path.GetFileNameWithoutExtension(name) + "-differenze.png"));
            results.Add(result);
        }

        var summary = new StringBuilder("| Schermata | Pixel diversi | Esito |\n|---|---|---|\n");
        foreach (var r in results)
            summary.AppendLine($"| {r.Name} | {(r.SizeChanged ? "dimensione cambiata" : $"{r.DifferentPercent:N3} %")} | {(r.Failed ? "❌ cambiata" : "✅")} |");
        File.WriteAllText(Path.Combine(report, "riepilogo.md"), summary.ToString());
        var step = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(step))
            File.AppendAllText(step, "## Confronto degli screenshot\n\n" + summary + "\n");

        var failed = results.Where(r => r.Failed).Select(r => r.Name).ToList();
        Assert.True(failed.Count == 0,
            $"Schermate cambiate rispetto ai riferimenti: {string.Join(", ", failed)}. Se il cambiamento è voluto, " +
            "copia i nuovi screenshot in tests/PcOrg.App.Tests/Riferimenti (vedi il LEGGIMI della cartella).");
    }

    /// <summary>Confronta due immagini; se sono diverse salva l'immagine delle differenze.</summary>
    public static Result Compare(string name, (int Width, int Height, byte[] Pixels) expected, (int Width, int Height, byte[] Pixels) actual, string? diffPath)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height)
            return new Result(name, 100, SizeChanged: true);

        var diff = new byte[actual.Pixels.Length];
        long different = 0;
        for (var i = 0; i < actual.Pixels.Length; i += 4)
        {
            var changed = false;
            for (var c = 0; c < 3; c++)
                if (Math.Abs(expected.Pixels[i + c] - actual.Pixels[i + c]) > ChannelTolerance)
                    changed = true;
            if (changed)
            {
                different++;
                diff[i] = 0; diff[i + 1] = 0; diff[i + 2] = 255; diff[i + 3] = 255; // rosso (BGRA)
            }
            else
            {
                // Immagine attuale schiarita, per orientarsi.
                for (var c = 0; c < 3; c++)
                    diff[i + c] = (byte)(200 + actual.Pixels[i + c] / 5);
                diff[i + 3] = 255;
            }
        }
        var percent = 100.0 * different / (actual.Width * (double)actual.Height);
        if (different > 0 && diffPath is not null)
        {
            var bitmap = BitmapSource.Create(actual.Width, actual.Height, 96, 96, PixelFormats.Bgra32, null, diff, actual.Width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(diffPath);
            encoder.Save(stream);
        }
        return new Result(name, percent, SizeChanged: false);
    }

    public static (int Width, int Height, byte[] Pixels) Load(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return (converted.PixelWidth, converted.PixelHeight, pixels);
    }

    [Fact]
    public void Tollera_piccole_differenze_e_segnala_quelle_grandi()
    {
        if (!OperatingSystem.IsWindows())
            return;
        static (int, int, byte[]) Plain(byte value) => (100, 100, Enumerable.Repeat(value, 100 * 100 * 4).ToArray());
        var a = Plain(200);
        var b = Plain(200);
        b.Item3[0] = 0; // un pixel diverso su 10.000: 0,01 %
        Assert.False(Compare("x", a, b, null).Failed);
        Assert.True(Compare("x", a, Plain(20), null).Failed);
        Assert.True(Compare("x", a, (50, 100, new byte[50 * 100 * 4]), null).SizeChanged);
    }
}
