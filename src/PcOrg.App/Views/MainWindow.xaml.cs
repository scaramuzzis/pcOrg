using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PcOrg.App.ViewModels;
using PcOrg.Core.Storage;

namespace PcOrg.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        CommandBindings.Add(new CommandBinding(FocusCommands.FocusSearch, (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }));
        Loaded += (_, _) => SearchBox.Focus();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void FileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.SelectedFiles = ResultsGrid.SelectedItems.OfType<FileRecord>().ToList();
    }

    private void ProductFilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.SelectedFiles = ProductFilesGrid.SelectedItems.OfType<ProductFileItem>().Select(i => i.File).ToList();
    }

    private void FileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var file = (sender as DataGrid)?.SelectedItem switch
        {
            FileRecord f => f,
            ProductFileItem item => item.File,
            _ => null,
        };
        if (file is not null)
            ViewModel?.OpenFileCommand.Execute(file);
    }

    private void SelectOldVersions_Click(object sender, RoutedEventArgs e)
    {
        ProductFilesGrid.SelectedItems.Clear();
        foreach (var item in ProductFilesGrid.Items.OfType<ProductFileItem>().Where(i => i.IsOld))
            ProductFilesGrid.SelectedItems.Add(item);
        ProductFilesGrid.Focus();
    }
}
