using System.Windows;
using PcOrg.Core.Moving;

namespace PcOrg.App.Views;

/// <summary>Mostra cosa verrà spostato e dove, prima di toccare qualsiasi file.</summary>
public partial class MovePreviewWindow : Window
{
    public MovePreviewWindow(IReadOnlyList<MoveItem> plan, string destination, bool destinationMonitored, string? summary = null)
    {
        InitializeComponent();
        PlanGrid.ItemsSource = plan;

        var toMove = plan.Count(p => p.WillMove);
        var renamed = plan.Count(p => p.Status == MoveItemStatus.Rinominato);
        var skipped = plan.Count - toMove;
        Summary.Text = (summary ?? $"{toMove} file andranno in \"{destination}\".")
            + (renamed > 0 ? $" {renamed} avranno un nuovo nome perché lì esiste già un file con lo stesso nome." : "")
            + (skipped > 0 ? $" {skipped} saranno ignorati." : "")
            + " Nessun file viene sovrascritto e potrai annullare tutto con un clic.";
        ConfirmButton.Label = $"Sposta {toMove} file";

        if (destinationMonitored)
            AddToMonitored.Visibility = Visibility.Collapsed;
    }

    public bool AddDestinationToMonitored =>
        AddToMonitored.Visibility == Visibility.Visible && AddToMonitored.IsChecked == true;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
