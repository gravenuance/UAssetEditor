using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using UAssetEditor.App.ViewModels;
using UAssetEditor.Core.AssetSources.PakWorker;

namespace UAssetEditor.App;

// CA1724: "App" colliding with the UAssetEditor.App namespace is the WPF project template's
// own standard shape (App.xaml's x:Class is generated from the project name) - every WPF
// app has this; renaming it would fight the SDK's own codegen, not fix anything.
#pragma warning disable CA1724
public partial class App : Application
#pragma warning restore CA1724
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
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
        _services?.GetService<MainViewModel>()?.Dispose();
        _services?.Dispose();

        // PakWorkerProcess.Shared owns a live child process (see its own doc comment) and is
        // never disposed by anything upstream of here - without this, the worker only notices
        // its pipe broke (and exits) once the OS gets around to closing this process's handles
        // during teardown, which is neither prompt nor guaranteed to run before the OS itself
        // considers this process gone. Disposing explicitly kills it deterministically instead.
        PakWorkerProcess.Shared.Dispose();

        base.OnExit(e);
    }
}
