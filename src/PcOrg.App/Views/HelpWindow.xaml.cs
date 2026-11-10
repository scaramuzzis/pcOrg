using System.IO;
using System.Windows;
using System.Windows.Controls;
using PcOrg.App.Infrastructure;

namespace PcOrg.App.Views;

/// <summary>Guida dell'applicazione: il testo di docs/GUIDA.md con indice dei capitoli e ricerca.</summary>
public partial class HelpWindow : Window
{
    private readonly List<HelpChapter> _chapters;

    public HelpWindow(string? chapter = null)
    {
        InitializeComponent();
        Viewer.Document = MarkdownFlow.Build(LoadGuide(), out _chapters);
        ChapterList.ItemsSource = _chapters;
        Loaded += (_, _) => ShowChapter(chapter);
    }

    public IReadOnlyList<HelpChapter> Chapters => _chapters;

    /// <summary>Testo della guida, incorporato nell'eseguibile.</summary>
    public static string LoadGuide()
    {
        using var stream = typeof(HelpWindow).Assembly.GetManifestResourceStream("PcOrg.App.GUIDA.md");
        if (stream is null)
            return "# Guida di pcOrg\n\nLa guida non è disponibile in questa versione.";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Porta in vista il capitolo il cui titolo contiene il testo indicato.</summary>
    public void ShowChapter(string? title)
    {
        var target = string.IsNullOrEmpty(title)
            ? null
            : _chapters.FirstOrDefault(c => c.Title.Contains(title, StringComparison.CurrentCultureIgnoreCase));
        if (target is not null)
            ChapterList.SelectedItem = target;
    }

    private void ChapterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChapterList.SelectedItem is HelpChapter chapter)
            chapter.Anchor.BringIntoView();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = SearchBox.Text.Trim();
        var matches = text.Length == 0
            ? _chapters
            : _chapters.Where(c => c.Text.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
        ChapterList.ItemsSource = matches;
        NoResults.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (text.Length > 0 && matches.Count > 0)
            ChapterList.SelectedIndex = 0;
    }
}
