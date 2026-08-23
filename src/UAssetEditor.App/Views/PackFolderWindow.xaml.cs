using System.Windows;

namespace UAssetEditor.App.Views;

public partial class PackFolderWindow : Window
{
    public PackFolderWindow() => InitializeComponent();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
