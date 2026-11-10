using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PcOrg.App.Infrastructure;

/// <summary>
/// Proprietà associata per eseguire un comando quando si preme Canc su una griglia
/// (es. "Sposta nel Cestino"). Il tasto viene intercettato solo se la griglia ha il focus,
/// quindi Canc continua a funzionare normalmente nelle caselle di testo.
/// </summary>
public static class GridKeys
{
    public static readonly DependencyProperty DeleteCommandProperty = DependencyProperty.RegisterAttached(
        "DeleteCommand", typeof(ICommand), typeof(GridKeys), new PropertyMetadata(null, OnChanged));

    public static ICommand? GetDeleteCommand(DependencyObject d) => (ICommand?)d.GetValue(DeleteCommandProperty);
    public static void SetDeleteCommand(DependencyObject d, ICommand? value) => d.SetValue(DeleteCommandProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
            return;
        grid.PreviewKeyDown -= OnKey;
        if (e.NewValue is not null)
            grid.PreviewKeyDown += OnKey;
    }

    private static void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || sender is not DataGrid grid || GetDeleteCommand(grid) is not { } command)
            return;
        if (command.CanExecute(null))
            command.Execute(null);
        e.Handled = true; // la griglia è di sola lettura: Canc non deve fare altro
    }
}
