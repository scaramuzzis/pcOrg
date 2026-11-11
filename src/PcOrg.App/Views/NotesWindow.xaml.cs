using System.Windows;
using System.Windows.Controls;
using PcOrg.Core.Storage;

namespace PcOrg.App.Views;

/// <summary>Modifica di etichette e nota dei file selezionati, con le etichette già usate a portata di clic.</summary>
public partial class NotesWindow : Window
{
    public NotesWindow(string title, string tags, string note, IReadOnlyList<string> knownTags)
    {
        InitializeComponent();
        Subtitle.Text = title;
        TagsBox.Text = tags;
        NoteBox.Text = note;
        foreach (var tag in knownTags.Take(20))
        {
            var button = new Button { Content = tag, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2, 8, 2), ToolTip = "Aggiungi questa etichetta" };
            button.Click += (_, _) => TagsBox.Text = FileIndex.NormalizeTags(TagsBox.Text + ", " + tag);
            KnownTags.Children.Add(button);
        }
        Loaded += (_, _) => TagsBox.Focus();
    }

    public string Tags => FileIndex.NormalizeTags(TagsBox.Text);
    public string Note => NoteBox.Text.Trim();

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
