using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace UAssetEditor.App.Views;

public partial class AboutWindow : Window
{
    private const string RepositoryUrl = "https://github.com/gravenuance/UAssetEditor";

    public AboutWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version == null ? "" : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    private void GitHubLink_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true });

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
