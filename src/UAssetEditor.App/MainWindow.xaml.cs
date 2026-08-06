using System.Windows;
using System.Windows.Input;
using UAssetEditor.App.ViewModels;

namespace UAssetEditor.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void AssetTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (AssetTree.SelectedItem is not AssetTreeItemViewModel item) return;

        if (viewModel.OpenFromTreeCommand.CanExecute(item))
            viewModel.OpenFromTreeCommand.Execute(item);
    }
}
