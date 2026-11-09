using System.Windows;
using PcOrg.App.Infrastructure;

namespace PcOrg.App.Views;

public enum DialogKind { Info, Success, Warning, Error, Question }

/// <summary>Finestra di dialogo moderna (icona colorata, titolo, testo, pulsanti) al posto dei MessageBox.</summary>
public partial class ModernDialog : Window
{
    internal ModernDialog(string title, string message, DialogKind kind, string primary, string? secondary)
    {
        InitializeComponent();
        Title = "pcOrg";
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primary;
        if (secondary is null)
            SecondaryButton.Visibility = Visibility.Collapsed;
        else
            SecondaryButton.Content = secondary;

        var (glyph, color) = kind switch
        {
            DialogKind.Success => (Glyphs.Success, "#16A34A"),
            DialogKind.Warning => (Glyphs.Warning, "#D97706"),
            DialogKind.Error => (Glyphs.Error, "#DC2626"),
            DialogKind.Question => (Glyphs.Question, "#2563EB"),
            _ => (Glyphs.Info, "#2563EB"),
        };
        IconGlyph.Text = glyph;
        IconBadge.Background = CategoryColors.Brush(color);
    }

    /// <summary>Mostra il dialogo; restituisce true se l'utente sceglie il pulsante principale.</summary>
    public static bool Show(Window? owner, string title, string message, DialogKind kind = DialogKind.Info,
        string primary = "OK", string? secondary = null)
    {
        var dialog = new ModernDialog(title, message, kind, primary, secondary);
        if (owner is { IsVisible: true })
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true;
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Secondary_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
