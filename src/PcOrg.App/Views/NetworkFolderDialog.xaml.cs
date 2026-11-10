using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace PcOrg.App.Views;

/// <summary>Richiesta del percorso di una cartella di rete.</summary>
public partial class NetworkFolderDialog : Window
{
    public NetworkFolderDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => PathBox.Focus();
    }

    public string FolderPath => PathBox.Text.Trim();

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e) =>
        AddButton.IsEnabled = FolderPath.Length > 2;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Scegli una cartella di rete" };
        if (dialog.ShowDialog(this) == true)
            PathBox.Text = Core.NetworkPaths.ToUnc(dialog.FolderName);
    }

    private void Add_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
