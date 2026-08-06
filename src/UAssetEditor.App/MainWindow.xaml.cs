using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using UAssetEditor.App.ViewModels;

namespace UAssetEditor.App;

public partial class MainWindow : Window
{
    // DWMWA_USE_IMMERSIVE_DARK_MODE: tells the OS compositor this window has a dark title
    // bar, so other native chrome it still draws (the system menu, Alt+Space, Windows 11's
    // snap-layout flyout) matches instead of flashing light-themed over a dark window. The
    // caption buttons themselves are hand-drawn below rather than left to DWM, since they
    // didn't reliably render over a WindowChrome-customized background in this configuration.
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var useDarkMode = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDarkMode, sizeof(int));
        };
    }

    private void AssetTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (AssetTree.SelectedItem is not AssetTreeItemViewModel item) return;

        if (viewModel.OpenFromTreeCommand.CanExecute(item))
            viewModel.OpenFromTreeCommand.Execute(item);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
