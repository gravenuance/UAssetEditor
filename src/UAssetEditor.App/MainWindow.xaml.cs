using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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

    // DefaultDllImportSearchPaths pins resolution to System32 (where dwmapi.dll actually
    // lives) instead of the default probing order, which includes the application's own
    // directory - without it, a same-named malicious DLL dropped next to the exe would be
    // loaded instead (CA5392/DLL planting).
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
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
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDarkMode, sizeof(int));
        };
    }

    private void AssetTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (AssetTree.SelectedItem is not AssetTreeItemViewModel item) return;

        // A no-op for any node kind other than Export (Folder/Asset/ExportsGroup just
        // expand/collapse via the TreeView's own default double-click behavior).
        if (viewModel.OpenFromTreeCommand.CanExecute(item))
            viewModel.OpenFromTreeCommand.Execute(item);
    }

    /// <summary>
    /// Lazily populates a tree node with its real children the first time it's expanded -
    /// an "Exports" node's per-export children, or an Export/Property node's own
    /// top-level property children (which may themselves be further expandable structs,
    /// arrays, or maps).
    /// </summary>
    private async void AssetTree_Expanded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (e.OriginalSource is not TreeViewItem { DataContext: AssetTreeItemViewModel item }) return;

        switch (item.Kind)
        {
            case TreeNodeKind.ExportsGroup:
                await viewModel.LoadExportsAsync(item);
                break;
            case TreeNodeKind.Export or TreeNodeKind.Property:
                await viewModel.LoadPropertiesAsync(item);
                break;
        }
    }

    /// <summary>A TreeViewItem doesn't select itself on a right click the way it does on a left click - this makes the row a context menu opens for the one visibly highlighted, instead of leaving whatever was selected before.</summary>
    private void TreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item) return;
        item.Focus();
        item.IsSelected = true;
    }

    /// <summary>Duplicates the right-clicked array element - see MainWindow.xaml's per-item ContextMenu, only attached to Property nodes where <see cref="AssetTreeItemViewModel.IsArrayElement"/> is true.</summary>
    private void DuplicateArrayElement_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (sender is not FrameworkElement { DataContext: AssetTreeItemViewModel item }) return;

        if (viewModel.DuplicateArrayElementCommand.CanExecute(item))
            viewModel.DuplicateArrayElementCommand.Execute(item);
    }

    /// <summary>Removes the right-clicked array element - the undo path for an unwanted <see cref="DuplicateArrayElement_Click"/>.</summary>
    private void RemoveArrayElement_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (sender is not FrameworkElement { DataContext: AssetTreeItemViewModel item }) return;

        if (viewModel.RemoveArrayElementCommand.CanExecute(item))
            viewModel.RemoveArrayElementCommand.Execute(item);
    }

    /// <summary>Clones the right-clicked top-level struct property as a brand new class member - see MainWindow.xaml's per-item ContextMenu, only attached to Property nodes where <see cref="AssetTreeItemViewModel.IsTopLevelStructProperty"/> is true.</summary>
    private void AddAnimGraphNode_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (sender is not FrameworkElement { DataContext: AssetTreeItemViewModel item }) return;

        if (viewModel.AddAnimGraphNodeCommand.CanExecute(item))
            viewModel.AddAnimGraphNodeCommand.Execute(item);
    }

    /// <summary>
    /// Every button with an attached ContextMenu (the File/Tools/Help menu-bar buttons, the
    /// Source "Browse..." split button, the "Recent" dropdown) opens it the same way on a
    /// left click instead of waiting for a right click - a ContextMenu is a separate popup
    /// root, so PlacementTarget has to be set explicitly for its items' bindings to reach
    /// back to the button's own DataContext.
    /// </summary>
    private void OpenAttachedContextMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
