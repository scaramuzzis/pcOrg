using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using PcOrg.App.Infrastructure;
using PcOrg.Core.Classification;
using PcOrg.Core.Drivers;
using PcOrg.Core.Storage;

namespace PcOrg.App.ViewModels;

/// <summary>Riquadro della Panoramica: un numero in evidenza, una riga di spiegazione e la scheda che apre.</summary>
public sealed record StatCard(string Title, string Value, string Detail, string Glyph, Brush Accent, string Tab);

/// <summary>Barra "spazio per categoria" della Panoramica.</summary>
public sealed record CategoryBar(FileCategory Category, string Name, string Glyph, Brush Accent, long Count, long Size, double Percent)
{
    public string SizeText => FileSizeConverter.Format(Size);
}

/// <summary>
/// Scheda "Panoramica": il colpo d'occhio iniziale. Riassume quanti file ci sono, quanto spazio si può
/// recuperare (versioni vecchie, cloni) e cosa c'è da fare per i driver, con un clic per andare al dettaglio.
/// </summary>
public sealed partial class MainViewModel
{
    public const int OverviewTab = 0, FilesTab = 1, ProductsTab = 2, DuplicatesTab = 3, DriversTab = 4;

    public ObservableCollection<StatCard> DashboardCards { get; } = [];
    public ObservableCollection<CategoryBar> CategoryBars { get; } = [];

    /// <summary>Statistiche per categoria dell'ultimo aggiornamento (per ricostruire la Panoramica).</summary>
    private IReadOnlyList<CategoryStat> _lastStats = [];

    /// <summary>Ricalcola riquadri e barre. Va chiamato quando cambiano file, prodotti, cloni o driver.</summary>
    private void UpdateDashboard()
    {
        var files = _lastStats.Sum(s => s.Count);
        var bytes = _lastStats.Sum(s => s.TotalSize);
        var offline = Roots.Count(r => r.IsOffline);
        var multi = Products.Where(p => p.Files.Count > 1).ToList();
        var oldBytes = multi.Sum(p => p.OldVersionsSize);
        var wasted = DuplicateGroups.Sum(g => g.WastedBytes);
        var updates = _allDevices.Count(d => d.HasUpdate);

        DashboardCards.Clear();
        DashboardCards.Add(new StatCard("File in ordine", files.ToString("N0"),
            HasScope ? $"{FileSizeConverter.Format(bytes)} nella cartella selezionata"
                : $"{FileSizeConverter.Format(bytes)} in {Roots.Count:N0} cartelle" + (offline > 0 ? $" · {offline} non raggiungibili" : ""),
            Glyphs.Folder, CategoryColors.Brush("#2563EB"), FilesTab.ToString()));
        DashboardCards.Add(new StatCard("Versioni vecchie", oldBytes > 0 ? FileSizeConverter.Format(oldBytes) : "Nessuna",
            multi.Count == 0 ? "Nessun programma in più versioni" : $"{multi.Count:N0} programmi presenti in più versioni",
            Glyphs.Package, CategoryColors.Brush("#7C3AED"), ProductsTab.ToString()));
        DashboardCards.Add(new StatCard("File identici", DuplicateGroups.Count > 0 ? FileSizeConverter.Format(wasted) : "Da cercare",
            DuplicateGroups.Count > 0 ? $"recuperabili da {DuplicateGroups.Count:N0} gruppi di cloni" : "Apri la scheda e premi \"Cerca i cloni\"",
            Glyphs.Duplicate, CategoryColors.Brush("#DB2777"), DuplicatesTab.ToString()));
        DashboardCards.Add(new StatCard("Driver", updates > 0 ? (updates == 1 ? "1 aggiornamento" : $"{updates:N0} aggiornamenti") : "A posto",
            _unneeded.Count == 1 ? "1 pacchetto non serve a questo PC" : _unneeded.Count > 0 ? $"{_unneeded.Count:N0} pacchetti non servono a questo PC" : "Nessun driver superfluo trovato",
            Glyphs.Device, CategoryColors.Brush("#16A34A"), DriversTab.ToString()));

        CategoryBars.Clear();
        var max = _lastStats.Count == 0 ? 0 : _lastStats.Max(s => s.TotalSize);
        foreach (var stat in _lastStats.OrderByDescending(s => s.TotalSize).ThenByDescending(s => s.Count))
            CategoryBars.Add(new CategoryBar(stat.Category, stat.Category.DisplayName(), Glyphs.ForCategory(stat.Category),
                CategoryColors.For(stat.Category), stat.Count, stat.TotalSize, max == 0 ? 0 : 100.0 * stat.TotalSize / max));
    }

    /// <summary>Clic su una barra della Panoramica: apre la scheda File filtrata per quella categoria.</summary>
    [RelayCommand]
    private void OpenCategory(CategoryBar? bar)
    {
        if (bar is null)
            return;
        SelectedCategory = Categories.FirstOrDefault(c => c.Category == bar.Category && !c.IsSubcategory) ?? SelectedCategory;
        SelectedTabIndex = FilesTab;
    }
}
