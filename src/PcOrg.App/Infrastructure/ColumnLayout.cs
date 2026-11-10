using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace PcOrg.App.Infrastructure;

/// <summary>
/// Salva e ripristina la disposizione delle colonne di una DataGrid: quali sono visibili,
/// in che ordine, quanto sono larghe e per quale colonna è ordinato l'elenco.
/// La colonna è identificata dal testo dell'intestazione.
/// </summary>
public static class ColumnLayout
{
    /// <summary>Testo dell'intestazione usato come chiave della colonna.</summary>
    public static string KeyOf(DataGridColumn column) => column.Header as string ?? column.SortMemberPath ?? "";

    /// <summary>La prima colonna definita (di solito il nome) resta sempre visibile.</summary>
    public static bool CanHide(DataGrid grid, DataGridColumn column) => grid.Columns.IndexOf(column) != 0;

    /// <summary>Fotografa lo stato attuale delle colonne.</summary>
    public static List<ColumnSetting> Capture(DataGrid grid) =>
        grid.Columns.Select(c => new ColumnSetting
        {
            Key = KeyOf(c),
            Visible = c.Visibility == Visibility.Visible,
            DisplayIndex = c.DisplayIndex,
            Width = c.Width.IsAuto ? 0 : c.Width.IsStar ? c.Width.Value : c.ActualWidth > 0 ? c.ActualWidth : c.Width.DisplayValue,
            WidthUnit = c.Width.IsStar ? "Star" : c.Width.IsAuto ? "Auto" : "Pixel",
            Sort = c.SortDirection?.ToString(),
        }).ToList();

    /// <summary>
    /// Applica uno stato salvato. Le colonne sconosciute (es. aggiunte in una versione successiva)
    /// mantengono le impostazioni predefinite e finiscono in fondo.
    /// </summary>
    public static void Apply(DataGrid grid, IReadOnlyList<ColumnSetting>? saved)
    {
        if (saved is null || saved.Count == 0)
            return;
        var byKey = saved.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.First());

        foreach (var column in grid.Columns)
        {
            if (!byKey.TryGetValue(KeyOf(column), out var s))
                continue;
            column.Visibility = s.Visible || !CanHide(grid, column) ? Visibility.Visible : Visibility.Collapsed;
            if (s.WidthUnit == "Star" && s.Width > 0)
                column.Width = new DataGridLength(s.Width, DataGridLengthUnitType.Star);
            else if (s.WidthUnit == "Pixel" && s.Width >= 20)
                column.Width = new DataGridLength(s.Width);
        }

        // Ordine: prima le colonne salvate nell'ordine salvato, poi le altre nell'ordine attuale.
        var ordered = grid.Columns
            .OrderBy(c => byKey.TryGetValue(KeyOf(c), out var s) ? s.DisplayIndex : int.MaxValue)
            .ThenBy(c => c.DisplayIndex)
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].DisplayIndex = i;

        // Ordinamento dell'elenco
        var sorted = grid.Columns.FirstOrDefault(c => byKey.TryGetValue(KeyOf(c), out var s) && s.Sort is not null);
        if (sorted is not null && Enum.TryParse<ListSortDirection>(byKey[KeyOf(sorted)].Sort, out var direction))
        {
            foreach (var c in grid.Columns)
                c.SortDirection = null;
            sorted.SortDirection = direction;
            var member = sorted.SortMemberPath;
            if (!string.IsNullOrEmpty(member))
            {
                grid.Items.SortDescriptions.Clear();
                grid.Items.SortDescriptions.Add(new SortDescription(member, direction));
            }
        }
    }
}
