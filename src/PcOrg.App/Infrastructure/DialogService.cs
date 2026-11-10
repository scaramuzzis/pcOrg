using System.Windows;
using Microsoft.Win32;
using PcOrg.App.Views;
using PcOrg.Core.Moving;
using PcOrg.Core.Storage;

namespace PcOrg.App.Infrastructure;

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current.MainWindow;

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public bool Confirm(string message, string title, string confirmText = "Conferma") =>
        ModernDialog.Show(Owner, title, message, DialogKind.Question, confirmText, "Annulla");

    public void Info(string message, string title) =>
        ModernDialog.Show(Owner, title, message, DialogKind.Info);

    public void Error(string message, string title) =>
        ModernDialog.Show(Owner, title, message, DialogKind.Warning);

    public MovePreviewResult? PreviewMove(IReadOnlyList<MoveItem> plan, string destination, bool destinationMonitored)
    {
        var window = new MovePreviewWindow(plan, destination, destinationMonitored) { Owner = Owner };
        return window.ShowDialog() == true ? new MovePreviewResult(window.AddDestinationToMonitored) : null;
    }

    public void ShowMoveHistory(FileIndex index, Action onChanged)
    {
        var window = new MoveHistoryWindow(index, this, onChanged) { Owner = Owner };
        window.ShowDialog();
    }
}
