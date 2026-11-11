using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PcOrg.Core.Content;
using PcOrg.Core.Moving;
using PcOrg.Core.Storage;
using PcOrg.Core.Updates;

namespace PcOrg.Core.Tests;

/// <summary>Ricerca nel contenuto (documenti, ZIP, ISO), etichette e note, controllo aggiornamenti.</summary>
public class ContentTests
{
    // ------------------------------------------------------------------ File di prova costruiti al volo

    private static void WriteDocx(string path, params string[] paragraphs)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), Encoding.UTF8);
        writer.Write("<?xml version=\"1.0\"?><w:document xmlns:w=\"x\"><w:body>");
        foreach (var p in paragraphs)
            writer.Write($"<w:p><w:r><w:t>{System.Net.WebUtility.HtmlEncode(p)}</w:t></w:r></w:p>");
        writer.Write("</w:body></w:document>");
    }

    /// <summary>PDF minimo di una pagina con testo non compresso e tabella xref corretta.</summary>
    private static void WritePdf(string path, string text)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            null!,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        var stream = $"BT /F1 12 Tf 72 720 Td ({text}) Tj ET";
        objects[3] = $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream";
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, sb.ToString(), Encoding.Latin1);
    }

    /// <summary>Immagine ISO 9660 minima: SETUP.EXE nella radice e DRIVERS/NVIDIA.INF (anche in versione Joliet).</summary>
    private static void WriteIso(string path, bool joliet)
    {
        const int sector = 2048;
        var image = new byte[sector * 24];
        byte[] Name(string s) => joliet ? Encoding.BigEndianUnicode.GetBytes(s) : Encoding.ASCII.GetBytes(s);
        byte[] Record(byte[] name, uint lba, uint size, bool dir)
        {
            var length = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
            var r = new byte[length];
            r[0] = (byte)length;
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(2), lba);
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(6), lba);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(10), size);
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(14), size);
            r[25] = (byte)(dir ? 2 : 0);
            r[32] = (byte)name.Length;
            name.CopyTo(r, 33);
            return r;
        }
        void Directory(int lba, params byte[][] records)
        {
            var pos = lba * sector;
            foreach (var r in new[] { Record([0], (uint)lba, sector, true), Record([1], (uint)lba, sector, true) }.Concat(records))
            {
                r.CopyTo(image, pos);
                pos += r.Length;
            }
        }
        void Descriptor(int lba, byte type, bool withJoliet)
        {
            var d = image.AsSpan(lba * sector, sector);
            d[0] = type;
            Encoding.ASCII.GetBytes("CD001").CopyTo(d[1..]);
            d[6] = 1;
            if (type == 255)
                return;
            if (withJoliet)
                new byte[] { 0x25, 0x2F, 0x45 }.CopyTo(d[88..]);
            Record([0], 20, sector, true).CopyTo(d[156..]);
        }
        Descriptor(16, joliet ? (byte)2 : (byte)1, joliet);
        Descriptor(17, 255, false);
        Directory(20, Record(Name("SETUP.EXE;1"), 22, 10, false), Record(Name(joliet ? "Driver Scheda Video" : "DRIVERS"), 21, sector, true));
        Directory(21, Record(Name(joliet ? "nvidia-566.36.inf;1" : "NVIDIA.INF;1"), 23, 10, false));
        File.WriteAllBytes(path, image);
    }

    // ------------------------------------------------------------------ Estrazione

    [Fact]
    public void Estrae_il_testo_da_word_pdf_e_testo_semplice()
    {
        using var dir = new TempDir();
        var docx = System.IO.Path.Combine(dir.Path, "contratto.docx");
        WriteDocx(docx, "Contratto di fornitura", "Il fornitore Rossi & C. consegna entro giugno");
        var pdf = System.IO.Path.Combine(dir.Path, "fattura.pdf");
        WritePdf(pdf, "Fattura numero 118 intestata a Bianchi");
        var txt = dir.File("note.txt", "Password del router: vedi cassetto");

        Assert.Contains("fornitore Rossi & C.", ContentExtractor.Extract(docx));
        Assert.Contains("Contratto di fornitura\n", ContentExtractor.Extract(docx)!.Replace("\r", ""));
        Assert.Contains("Bianchi", ContentExtractor.Extract(pdf));
        Assert.Contains("router", ContentExtractor.Extract(txt));
        Assert.Null(ContentExtractor.Extract(dir.File("rotto.pdf", "non sono un pdf")));
        Assert.Null(ContentExtractor.Extract(dir.File("rotto.docx", "non sono uno zip")));
    }

    [Theory]
    [InlineData(false, "SETUP.EXE", "DRIVERS/NVIDIA.INF")]
    [InlineData(true, "SETUP.EXE", "Driver Scheda Video/nvidia-566.36.inf")]
    public void Elenca_i_file_di_una_iso(bool joliet, string first, string second)
    {
        using var dir = new TempDir();
        var iso = System.IO.Path.Combine(dir.Path, "disco.iso");
        WriteIso(iso, joliet);

        Assert.Equal([first, second], IsoReader.ListFiles(iso));
        Assert.Contains(second, ContentExtractor.Extract(iso));
        Assert.Empty(IsoReader.ListFiles(new MemoryStream(new byte[100_000]))); // non è una ISO
    }

    // ------------------------------------------------------------------ Indice

    private static (FileIndex Index, RootFolder Root, TempDir Dir) Library()
    {
        var dir = new TempDir();
        var folder = dir.Dir("Documenti");
        WriteDocx(System.IO.Path.Combine(folder, "contratto.docx"), "Contratto di fornitura con la ditta Rossi", "Consegna entro giugno");
        WritePdf(System.IO.Path.Combine(folder, "scansione0042.pdf"), "Fattura numero 118 intestata a Bianchi");
        dir.File("Documenti/appunti.txt", "Ricordarsi di rinnovare l'assicurazione");
        using (var zip = ZipFile.Open(System.IO.Path.Combine(folder, "pacchetto.zip"), ZipArchiveMode.Create))
        {
            zip.CreateEntry("driver/realtek-audio-setup.exe");
            zip.CreateEntry("leggimi.txt");
        }
        WriteIso(System.IO.Path.Combine(folder, "backup.iso"), joliet: true);
        var index = FileIndex.Open(System.IO.Path.Combine(dir.Path, "index.db"));
        var root = index.AddRoot(folder);
        index.UpdateRoot(root.Id, root.Path);
        return (index, root, dir);
    }

    [Fact]
    public void Trova_i_documenti_dal_testo_e_gli_archivi_dal_contenuto()
    {
        var (index, _, dir) = Library();
        using (dir)
        {
            Assert.Equal(5, index.CountFilesNeedingContent());
            Assert.Equal(5, ContentIndexer.Run(index));
            Assert.Equal(0, index.CountFilesNeedingContent());
            Assert.Equal(5, index.CountIndexedContent());

            // Senza "nel contenuto" il nome non corrisponde
            Assert.Empty(index.Search(new SearchQuery("bianchi")));

            var pdf = Assert.Single(index.Search(new SearchQuery("bianchi") { InContent = true }));
            Assert.Equal("scansione0042.pdf", pdf.Name);
            Assert.Contains("«Bianchi»", pdf.Snippet);

            // Più parole, inizio di parola, maiuscole e accenti ignorati
            Assert.Equal("contratto.docx", Assert.Single(index.Search(new SearchQuery("ROSSI fornit") { InContent = true })).Name);
            Assert.Equal("appunti.txt", Assert.Single(index.Search(new SearchQuery("assicurazione") { InContent = true })).Name);
            // Archivi: file contenuti in ZIP e ISO
            Assert.Equal("pacchetto.zip", Assert.Single(index.Search(new SearchQuery("realtek") { InContent = true })).Name);
            Assert.Equal("backup.iso", Assert.Single(index.Search(new SearchQuery("nvidia") { InContent = true })).Name);
            // Filtro per categoria anche sul contenuto
            Assert.Empty(index.Search(new SearchQuery("bianchi", Classification.FileCategory.Word) { InContent = true }));
            // Caratteri speciali nella ricerca non devono dare errori
            Assert.Empty(index.Search(new SearchQuery("\"(xyz* OR -") { InContent = true }));
        }
    }

    [Fact]
    public void Il_contenuto_segue_le_modifiche_e_le_eliminazioni()
    {
        var (index, root, dir) = Library();
        using (dir)
        {
            ContentIndexer.Run(index);
            var txt = System.IO.Path.Combine(root.Path, "appunti.txt");
            File.WriteAllText(txt, "Adesso si parla di tutt'altro argomento: le bollette");
            File.Delete(System.IO.Path.Combine(root.Path, "scansione0042.pdf"));
            index.UpdateRoot(root.Id, root.Path, full: true);

            Assert.Equal(1, ContentIndexer.Run(index)); // solo il file modificato
            Assert.Empty(index.Search(new SearchQuery("assicurazione") { InContent = true }));
            Assert.Single(index.Search(new SearchQuery("bollette") { InContent = true }));
            Assert.Empty(index.Search(new SearchQuery("bianchi") { InContent = true })); // file eliminato
        }
    }

    // ------------------------------------------------------------------ Etichette e note

    [Fact]
    public void Etichette_e_note_si_cercano_seguono_gli_spostamenti_e_la_ricostruzione()
    {
        var (index, root, dir) = Library();
        using (dir)
        {
            var path = System.IO.Path.Combine(root.Path, "scansione0042.pdf");
            index.SetNotes([path], " ufficio ,Fatture, ufficio", "Da pagare entro fine mese");

            var file = Assert.Single(index.Search(new SearchQuery("fatture")));
            Assert.Equal("ufficio, Fatture", file.Tags);
            Assert.Equal("Da pagare entro fine mese", file.Note);
            Assert.Single(index.Search(new SearchQuery("pagare")));
            Assert.Equal([("Fatture", 1), ("ufficio", 1)], index.GetTags());

            // Solo la nota (le etichette restano)
            index.SetNotes([path], null, "Pagata");
            Assert.Equal("ufficio, Fatture", Assert.Single(index.Search(new SearchQuery("pagata"))).Tags);

            // Spostamento: etichette e nota seguono il file
            var target = dir.Dir("Documenti/Archivio");
            new MoveService(index).Execute(MovePlanner.Plan([path], target), "test");
            var moved = Assert.Single(index.Search(new SearchQuery("ufficio")));
            Assert.Equal(System.IO.Path.Combine(target, "scansione0042.pdf"), moved.Path);

            // Ricostruzione dell'indice: restano perché legate al percorso
            index.ClearFiles();
            index.UpdateRoot(root.Id, root.Path);
            Assert.Equal("Pagata", Assert.Single(index.Search(new SearchQuery("ufficio"))).Note);

            // Etichette e nota vuote: rimosse
            index.SetNotes([moved.Path], "", "");
            Assert.Empty(index.Search(new SearchQuery("ufficio")));
            Assert.Empty(index.GetTags());
        }
    }

    // ------------------------------------------------------------------ Aggiornamenti

    [Theory]
    [InlineData("v0.15.0", "0.14.0", UpdateStatus.NuovaVersione)]
    [InlineData("v0.14.0", "0.14.0", UpdateStatus.Aggiornato)]
    [InlineData("v0.9.0", "0.14.0", UpdateStatus.Aggiornato)] // 0.9 è più vecchia di 0.14 (confronto numerico)
    public void Confronta_la_versione_pubblicata(string tag, string current, UpdateStatus expected)
    {
        var info = UpdateChecker.Parse($$"""{"tag_name":"{{tag}}","html_url":"https://example.invalid/r"}""", current);
        Assert.Equal(expected, info.Status);
        Assert.Equal(tag.TrimStart('v'), info.LatestVersion);
    }

    [Fact]
    public async Task Senza_rete_il_controllo_non_fallisce()
    {
        using var client = new HttpClient(new FailingHandler());
        var info = await UpdateChecker.CheckAsync("0.14.0", client);
        Assert.Equal(UpdateStatus.NonDisponibile, info.Status);
        Assert.NotNull(info.Error);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("rete assente");
    }
}
