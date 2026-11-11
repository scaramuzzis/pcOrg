using PcOrg.Core.Moving;

namespace PcOrg.App.Infrastructure;

/// <summary>Finestre e conferme, separate dal ViewModel per tenerlo testabile.</summary>
public interface IDialogService
{
    string? PickFolder(string title);

    /// <summary>Chiede dove salvare un file. Null se l'utente annulla.</summary>
    string? PickSaveFile(string title, string defaultName, string filter);

    /// <summary>Apre la guida, eventualmente al capitolo indicato.</summary>
    void ShowHelp(string? chapter = null);

    /// <summary>Chiede etichette e nota per i file selezionati. Null se l'utente annulla.</summary>
    (string Tags, string Note)? EditNotes(string title, string tags, string note, IReadOnlyList<string> knownTags);

    /// <summary>Finestra "Informazioni su pcOrg".</summary>
    void ShowAbout();

    /// <summary>Chiede il percorso di una cartella di rete (es. \\NAS\Foto). Null se l'utente annulla.</summary>
    string? AskNetworkFolder();

    /// <summary>Chiede conferma; <paramref name="confirmText"/> è il testo del pulsante principale.</summary>
    bool Confirm(string message, string title, string confirmText = "Conferma");

    void Info(string message, string title);
    void Error(string message, string title);

    /// <summary>Mostra l'anteprima degli spostamenti. Restituisce null se l'utente annulla.</summary>
    /// <param name="summary">Testo introduttivo al posto di "N file andranno in …" (es. per le regole).</param>
    MovePreviewResult? PreviewMove(IReadOnlyList<MoveItem> plan, string destination, bool destinationMonitored, string? summary = null);

    void ShowMoveHistory(Core.Storage.FileIndex index, Action onChanged);
}

/// <summary>Scelte fatte dall'utente nella finestra di anteprima.</summary>
public sealed record MovePreviewResult(bool AddDestinationToMonitored);
