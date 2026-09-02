using System.Windows;

namespace UAssetEditor.App.Views;

public partial class ConvertIoStoreToLegacyWindow : Window
{
    public ConvertIoStoreToLegacyWindow() => InitializeComponent();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
