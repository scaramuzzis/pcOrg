using PcOrg.Core.Moving;

namespace PcOrg.App.Infrastructure;

/// <summary>Finestre e conferme, separate dal ViewModel per tenerlo testabile.</summary>
public interface IDialogService
{
    string? PickFolder(string title);
    bool Confirm(string message, string title);
    void Info(string message, string title);
    void Error(string message, string title);

    /// <summary>Mostra l'anteprima degli spostamenti. Restituisce null se l'utente annulla.</summary>
    MovePreviewResult? PreviewMove(IReadOnlyList<MoveItem> plan, string destination, bool destinationMonitored);

    void ShowMoveHistory(Core.Storage.FileIndex index, Action onChanged);
}

/// <summary>Scelte fatte dall'utente nella finestra di anteprima.</summary>
public sealed record MovePreviewResult(bool AddDestinationToMonitored);
