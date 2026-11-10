using System.Windows;
using Microsoft.Win32;
using PcOrg.App.Views;
using PcOrg.Core.Moving;
using PcOrg.Core.Storage;

namespace PcOrg.App.Infrastructure;

/// <summary>Implementazione reale delle finestre di dialogo (scelta cartelle, conferme, guida...).</summary>
public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current.MainWindow;

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string? PickSaveFile(string title, string defaultName, string filter)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = defaultName, Filter = filter, AddExtension = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    private HelpWindow? _help;

    /// <summary>La guida resta aperta accanto all'applicazione; una sola finestra alla volta.</summary>
    public void ShowHelp(string? chapter = null)
    {
        if (_help is { IsLoaded: true })
        {
            _help.ShowChapter(chapter);
            _help.Activate();
            return;
        }
        _help = new HelpWindow(chapter);
        if (Owner is { IsVisible: true })
            _help.Owner = Owner;
        _help.Closed += (_, _) => _help = null;
        _help.Show();
    }

    public void ShowAbout()
    {
        var window = new AboutWindow();
        if (Owner is { IsVisible: true })
            window.Owner = Owner;
        window.ShowDialog();
    }

    public string? AskNetworkFolder()
    {
        var dialog = new NetworkFolderDialog();
        if (Owner is { IsVisible: true })
            dialog.Owner = Owner;
        return dialog.ShowDialog() == true ? dialog.FolderPath : null;
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
