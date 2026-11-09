using System.Windows;
using System.Windows.Controls;
using PcOrg.App.Infrastructure;
using PcOrg.Core.Moving;
using PcOrg.Core.Storage;

namespace PcOrg.App.Views;

/// <summary>Elenco degli spostamenti fatti, con possibilità di riportare i file dov'erano.</summary>
public partial class MoveHistoryWindow : Window
{
    private readonly FileIndex _index;
    private readonly IDialogService _dialogs;
    private readonly Action _onChanged;

    public MoveHistoryWindow(FileIndex index, IDialogService dialogs, Action onChanged)
    {
        InitializeComponent();
        _index = index;
        _dialogs = dialogs;
        _onChanged = onChanged;
        Reload();
    }

    private void Reload() => BatchGrid.ItemsSource = _index.GetMoveBatches(200);

    private void BatchGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var batch = BatchGrid.SelectedItem as MoveBatch;
        EntryGrid.ItemsSource = batch is null ? null : _index.GetMoveLog(batch.Id);
        UndoButton.IsEnabled = batch is { Undone: false };
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (BatchGrid.SelectedItem is not MoveBatch batch)
            return;
        if (!_dialogs.Confirm($"Riportare i {batch.ItemCount} file nella posizione originale?", "Annulla spostamento"))
            return;

        var result = new MoveService(_index).Undo(batch.Id);
        if (result.Errors.Count == 0)
            _dialogs.Info($"Ripristinati {result.Moved} file.", "Annulla spostamento");
        else
            _dialogs.Error($"Ripristinati {result.Moved} file. {result.Errors.Count} non sono stati ripristinati:\n\n"
                + string.Join("\n", result.Errors.Take(15)), "Annullamento incompleto");

        Reload();
        _onChanged();
    }
}
