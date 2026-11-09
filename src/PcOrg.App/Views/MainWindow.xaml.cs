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

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.SelectedFiles = ResultsGrid.SelectedItems.OfType<FileRecord>().ToList();
    }

    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is FileRecord file)
            ViewModel?.OpenFileCommand.Execute(file);
    }
}
