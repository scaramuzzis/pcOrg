using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PcOrg.App.Infrastructure;

/// <summary>Un capitolo della guida (titolo "## ..."), con il blocco a cui saltare e il testo per la ricerca.</summary>
public sealed record HelpChapter(string Title, Block Anchor, string Text);

/// <summary>
/// Converte un Markdown semplice (titoli, paragrafi, elenchi, citazioni, tabelle, **grassetto**, `codice`)
/// in un FlowDocument WPF, con i colori del tema attivo.
/// </summary>
public static partial class MarkdownFlow
{
    [GeneratedRegex(@"(\*\*[^*]+\*\*|`[^`]+`)")]
    private static partial Regex InlineTokens();

    [GeneratedRegex(@"^\d+\.\s+")]
    private static partial Regex NumberedItem();

    public static FlowDocument Build(string markdown, out List<HelpChapter> chapters)
    {
        var found = new List<HelpChapter>();
        chapters = found;
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            LineHeight = 22,
            PagePadding = new Thickness(36, 24, 36, 40),
            TextAlignment = TextAlignment.Left,
        };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        HelpChapter? current = null;
        var chapterText = new System.Text.StringBuilder();
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
            AddInlines(p.Inlines, string.Join(' ', paragraph));
            doc.Blocks.Add(p);
            paragraph.Clear();
        }

        void CloseChapter()
        {
            if (current is not null)
                found.Add(current with { Text = chapterText.ToString() });
            chapterText.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (current is not null)
                chapterText.AppendLine(line);

            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (line.StartsWith('#'))
            {
                FlushParagraph();
                var level = line.TakeWhile(c => c == '#').Count();
                var title = line[level..].Trim();
                var heading = new Paragraph(new Run(title))
                {
                    FontWeight = FontWeights.SemiBold,
                    FontSize = level switch { 1 => 28, 2 => 21, _ => 16 },
                    Margin = level == 1 ? new Thickness(0, 0, 0, 14) : new Thickness(0, 22, 0, 8),
                };
                if (level == 2)
                {
                    heading.BorderThickness = new Thickness(0, 0, 0, 1);
                    heading.SetResourceReference(Block.BorderBrushProperty, "CardStrokeColorDefaultBrush");
                    heading.Padding = new Thickness(0, 0, 0, 6);
                }
                doc.Blocks.Add(heading);
                if (level == 1)
                {
                    CloseChapter();
                    current = new HelpChapter("Introduzione", heading, "");
                }
                else if (level == 2)
                {
                    CloseChapter();
                    current = new HelpChapter(title, heading, "");
                    chapterText.AppendLine(title);
                }
                continue;
            }

            if (line.StartsWith('|'))
            {
                FlushParagraph();
                var rows = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                {
                    rows.Add(lines[i].Trim());
                    if (current is not null && rows.Count > 1)
                        chapterText.AppendLine(lines[i]);
                    i++;
                }
                i--;
                doc.Blocks.Add(BuildTable(rows));
                continue;
            }

            if (line.StartsWith("- ") || NumberedItem().IsMatch(line) || line.StartsWith("  - "))
            {
                FlushParagraph();
                var numbered = NumberedItem().IsMatch(line);
                var list = new List
                {
                    MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, 12),
                    Padding = new Thickness(24, 0, 0, 0),
                };
                ListItem? last = null;
                while (i < lines.Length)
                {
                    var item = lines[i].TrimEnd();
                    if (item.StartsWith("   - ") || item.StartsWith("  - "))
                    {
                        // sotto-elenco dentro la voce precedente
                        var sub = last?.Blocks.LastBlock as List;
                        if (sub is null)
                        {
                            sub = new List { MarkerStyle = TextMarkerStyle.Circle, Padding = new Thickness(20, 0, 0, 0), Margin = new Thickness(0, 4, 0, 0) };
                            last?.Blocks.Add(sub);
                        }
                        var subParagraph = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                        AddInlines(subParagraph.Inlines, item.Trim()[2..]);
                        sub.ListItems.Add(new ListItem(subParagraph));
                    }
                    else if (numbered ? NumberedItem().IsMatch(item) : item.StartsWith("- "))
                    {
                        var text = numbered ? NumberedItem().Replace(item, "") : item[2..];
                        var p = new Paragraph { Margin = new Thickness(0, 3, 0, 3) };
                        AddInlines(p.Inlines, text);
                        last = new ListItem(p);
                        list.ListItems.Add(last);
                    }
                    else
                    {
                        break;
                    }
                    if (current is not null && i + 1 < lines.Length)
                        chapterText.AppendLine(lines[i + 1]);
                    i++;
                }
                i--;
                doc.Blocks.Add(list);
                continue;
            }

            if (line.StartsWith('>'))
            {
                FlushParagraph();
                var note = new Paragraph
                {
                    Margin = new Thickness(0, 4, 0, 14),
                    Padding = new Thickness(14, 10, 14, 10),
                    BorderThickness = new Thickness(4, 0, 0, 0),
                    BorderBrush = CategoryColors.Brush("#2563EB"),
                };
                note.SetResourceReference(Block.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                AddInlines(note.Inlines, line.TrimStart('>', ' '));
                doc.Blocks.Add(note);
                continue;
            }

            paragraph.Add(line.Trim());
        }
        FlushParagraph();
        CloseChapter();
        return doc;
    }

    private static Table BuildTable(List<string> rows)
    {
        static string[] Cells(string row) => row.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        var header = Cells(rows[0]);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 16), BorderThickness = new Thickness(1) };
        table.SetResourceReference(Table.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        for (var c = 0; c < header.Length; c++)
            table.Columns.Add(new TableColumn { Width = c == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(2, GridUnitType.Star) });

        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        var dataRows = rows.Where((_, index) => index != 1 || !rows[1].Contains("---")).ToList();
        for (var r = 0; r < dataRows.Count; r++)
        {
            var row = new TableRow();
            if (r == 0)
                row.SetResourceReference(TableRow.BackgroundProperty, "SubtleFillColorSecondaryBrush");
            foreach (var cell in Cells(dataRows[r]))
            {
                var p = new Paragraph { Margin = new Thickness(0) };
                AddInlines(p.Inlines, cell);
                if (r == 0)
                    p.FontWeight = FontWeights.SemiBold;
                var tableCell = new TableCell(p) { Padding = new Thickness(10, 6, 10, 6), BorderThickness = new Thickness(0, 0, 0, 1) };
                tableCell.SetResourceReference(TableCell.BorderBrushProperty, "CardStrokeColorDefaultBrush");
                row.Cells.Add(tableCell);
            }
            group.Rows.Add(row);
        }
        return table;
    }

    /// <summary>Testo con **grassetto** e `codice`.</summary>
    private static void AddInlines(InlineCollection inlines, string text)
    {
        foreach (var part in InlineTokens().Split(text))
        {
            if (part.Length == 0)
                continue;
            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                inlines.Add(new Bold(new Run(part[2..^2])));
            else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2)
            {
                var code = new Run(part[1..^1]) { FontFamily = new FontFamily("Cascadia Mono, Consolas") };
                code.SetResourceReference(TextElement.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                inlines.Add(code);
            }
            else
                inlines.Add(new Run(part));
        }
    }
}
