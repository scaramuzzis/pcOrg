using System.Windows;
using PcOrg.Core.Moving;

namespace PcOrg.App.Views;

/// <summary>Mostra cosa verrà spostato e dove, prima di toccare qualsiasi file.</summary>
public partial class MovePreviewWindow : Window
{
    public MovePreviewWindow(IReadOnlyList<MoveItem> plan, string destination, bool destinationMonitored)
    {
        InitializeComponent();
        PlanGrid.ItemsSource = plan;

        var toMove = plan.Count(p => p.WillMove);
        var renamed = plan.Count(p => p.Status == MoveItemStatus.Rinominato);
        var skipped = plan.Count - toMove;
        Summary.Text = $"Verranno spostati {toMove} file in \"{destination}\"."
            + (renamed > 0 ? $" {renamed} avranno un nuovo nome perché esiste già un file con lo stesso nome." : "")
            + (skipped > 0 ? $" {skipped} saranno ignorati." : "")
            + " Nessun file viene sovrascritto e l'operazione si può annullare dalla cronologia.";
        ConfirmButton.Content = $"Sposta {toMove} file";

        if (destinationMonitored)
            AddToMonitored.Visibility = Visibility.Collapsed;
    }

    public bool AddDestinationToMonitored =>
        AddToMonitored.Visibility == Visibility.Visible && AddToMonitored.IsChecked == true;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
