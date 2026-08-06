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

        _services.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.GetService<MainViewModel>()?.Cleanup();
        _services?.Dispose();
        base.OnExit(e);
    }
}
