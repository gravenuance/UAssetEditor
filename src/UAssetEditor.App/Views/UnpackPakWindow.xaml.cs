using System.Windows;

namespace UAssetEditor.App.Views;

public partial class UnpackPakWindow : Window
{
    public UnpackPakWindow() => InitializeComponent();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
