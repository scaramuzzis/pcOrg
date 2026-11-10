using PcOrg.Core.Moving;

namespace PcOrg.App.Infrastructure;

/// <summary>Finestre e conferme, separate dal ViewModel per tenerlo testabile.</summary>
public interface IDialogService
{
    string? PickFolder(string title);

    /// <summary>Chiede dove salvare un file. Null se l'utente annulla.</summary>
    string? PickSaveFile(string title, string defaultName, string filter);

    /// <summary>Finestra "Informazioni su pcOrg".</summary>
    void ShowAbout();

    /// <summary>Chiede il percorso di una cartella di rete (es. \\NAS\Foto). Null se l'utente annulla.</summary>
    string? AskNetworkFolder();

    /// <summary>Chiede conferma; <paramref name="confirmText"/> è il testo del pulsante principale.</summary>
    bool Confirm(string message, string title, string confirmText = "Conferma");

    void Info(string message, string title);
    void Error(string message, string title);

    /// <summary>Mostra l'anteprima degli spostamenti. Restituisce null se l'utente annulla.</summary>
    MovePreviewResult? PreviewMove(IReadOnlyList<MoveItem> plan, string destination, bool destinationMonitored);

    void ShowMoveHistory(Core.Storage.FileIndex index, Action onChanged);
}

/// <summary>Scelte fatte dall'utente nella finestra di anteprima.</summary>
public sealed record MovePreviewResult(bool AddDestinationToMonitored);
