using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PcOrg.Core.Classification;
using PcOrg.Core.Rules;

namespace PcOrg.App.Views;

/// <summary>Creazione o modifica di una regola di archiviazione.</summary>
public partial class RuleEditorWindow : Window
{
    private readonly ArchiveRule _rule;

    /// <param name="rule">Copia della regola da modificare: viene aggiornata solo con "Salva la regola".</param>
    public RuleEditorWindow(ArchiveRule rule)
    {
        InitializeComponent();
        _rule = rule;
        RuleName.Text = rule.Name;
        CategoryChooser.Items.Add(new ComboBoxItem { Content = "Qualsiasi categoria", Tag = null });
        foreach (var category in Enum.GetValues<FileCategory>().OrderBy(c => c.DisplayName()))
            CategoryChooser.Items.Add(new ComboBoxItem { Content = category.DisplayName(), Tag = category });
        CategoryChooser.SelectedItem = CategoryChooser.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, rule.Category))
                                       ?? CategoryChooser.Items[0];
        Company.Text = rule.CompanyContains ?? "";
        NameContains.Text = rule.NameContains ?? "";
        Destination.Text = rule.Destination;
        EnabledBox.IsChecked = rule.Enabled;

        foreach (var placeholder in RuleEngine.Placeholders)
        {
            var button = new Button { Content = placeholder, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2, 8, 2) };
            button.Click += (_, _) =>
            {
                var caret = Destination.CaretIndex;
                Destination.Text = Destination.Text.Insert(caret, placeholder);
                Destination.CaretIndex = caret + placeholder.Length;
                Destination.Focus();
            };
            PlaceholderButtons.Children.Add(button);
        }
        UpdateExample();
        Loaded += (_, _) => RuleName.Focus();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Cartella di destinazione della regola" };
        if (dialog.ShowDialog(this) == true)
            Destination.Text = dialog.FolderName;
    }

    private void Destination_TextChanged(object sender, TextChangedEventArgs e) => UpdateExample();

    /// <summary>Mostra dove finirebbe un file di esempio, per capire subito l'effetto dei segnaposto.</summary>
    private void UpdateExample()
    {
        if (ExampleText is null)
            return;
        var sample = new Core.Storage.FileRecord(0, 0, @"C:\Download\566.36-driver.exe", "566.36-driver.exe", ".exe", @"C:\Download", 1,
            new DateTime(2024, 11, 5, 10, 0, 0, DateTimeKind.Utc), FileCategory.Driver,
            new Core.Metadata.FileMeta { ProductName = "NVIDIA Graphics Driver", CompanyName = "NVIDIA Corporation", ProductVersion = "566.36" });
        var preview = new ArchiveRule { Destination = Destination.Text };
        ExampleText.Text = string.IsNullOrWhiteSpace(Destination.Text)
            ? "Indica la cartella in cui spostare i file."
            : $"Esempio: un driver NVIDIA 566.36 finirebbe in {RuleEngine.ResolveDestination(preview, sample)}";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Destination.Text))
        {
            ModernDialog.Show(this, "Manca la destinazione", "Indica la cartella in cui la regola deve spostare i file.", DialogKind.Warning);
            return;
        }
        _rule.Name = string.IsNullOrWhiteSpace(RuleName.Text) ? "Regola senza nome" : RuleName.Text.Trim();
        _rule.Category = (CategoryChooser.SelectedItem as ComboBoxItem)?.Tag as FileCategory?;
        _rule.CompanyContains = string.IsNullOrWhiteSpace(Company.Text) ? null : Company.Text.Trim();
        _rule.NameContains = string.IsNullOrWhiteSpace(NameContains.Text) ? null : NameContains.Text.Trim();
        _rule.Destination = Destination.Text.Trim();
        _rule.Enabled = EnabledBox.IsChecked == true;
        DialogResult = true;
    }
}
