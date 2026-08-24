using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UAssetEditor.App.ViewModels;

namespace UAssetEditor.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var collection = new ServiceCollection();
        collection.AddSingleton<MainViewModel>();
        collection.AddSingleton<MainWindow>();
        _services = collection.BuildServiceProvider();

        var mainViewModel = _services.GetRequiredService<MainViewModel>();
        ApplyLaunchArgs(mainViewModel, e.Args);

        _services.GetRequiredService<MainWindow>().Show();
    }

    /// <summary>
    /// Lets Windows file association ("Open with" -&gt; this app, or "Always use this app")
    /// hand a double-clicked .pak/.uasset straight to the editor - just fills in the Source
    /// field (overriding whatever the last session restored), the same as browsing to it by
    /// hand. Deliberately doesn't auto-load: version/AES/usmap are per-game and this app has
    /// no way to guess them, so the user still picks those and clicks Load themselves.
    /// </summary>
    private static void ApplyLaunchArgs(MainViewModel mainViewModel, string[] args)
    {
        var launchPath = args.FirstOrDefault(a =>
            File.Exists(a) && (a.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)));

        if (launchPath != null)
            mainViewModel.SourcePath = launchPath;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetService<MainViewModel>()?.Cleanup();
        _services?.Dispose();
        base.OnExit(e);
    }
}
