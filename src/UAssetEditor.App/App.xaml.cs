using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UAssetEditor.App.ViewModels;
using UAssetEditor.Core.AssetSources.PakWorker;
using UAssetEditor.Core.Logging;

namespace UAssetEditor.App;

// CA1724: "App" colliding with the UAssetEditor.App namespace is the WPF project template's
// own standard shape (App.xaml's x:Class is generated from the project name) - every WPF
// app has this; renaming it would fight the SDK's own codegen, not fix anything.
// CA1001: WPF's Application base isn't IDisposable, so this can't implement IDisposable itself
// the usual way - _loggerFactory (which owns and disposes the file logger provider) is
// explicitly disposed in OnExit instead, WPF's own equivalent shutdown hook.
#pragma warning disable CA1724, CA1001
public partial class App : Application
#pragma warning restore CA1724, CA1001
{
    private ServiceProvider? _services;
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private ILogger<App> _logger = NullLogger<App>.Instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);

        InitializeLogging();
        InstallCrashHandlers();

        var collection = new ServiceCollection();
        collection.AddSingleton<MainViewModel>();
        collection.AddSingleton<MainWindow>();
        _services = collection.BuildServiceProvider();

        var mainViewModel = _services.GetRequiredService<MainViewModel>();
        ApplyLaunchArgs(mainViewModel, e.Args);

        _logger.LogInformation("UAssetEditor started.");
        _services.GetRequiredService<MainWindow>().Show();
    }

    /// <summary>
    /// A rotating plain-text log under LocalAppData - the in-app StatusMessage text disappears
    /// the moment the window closes, so this is what's actually left to hand over after a
    /// crash or a hard-to-reproduce bug. <see cref="AppLog"/> gives every ViewModel access to
    /// it without threading an ILoggerFactory through every constructor (see its own doc
    /// comment for why that fits this app's shape).
    /// </summary>
    private void InitializeLogging()
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UAssetEditor", "Logs");

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new FileLoggerProvider(logDirectory));
            builder.SetMinimumLevel(LogLevel.Information);
        });
        AppLog.Initialize(_loggerFactory);
        _logger = _loggerFactory.CreateLogger<App>();
    }

    /// <summary>
    /// Writes a final record before the process goes down, whichever of the three ways .NET/WPF
    /// can fail unexpectedly is the one that happens: a WPF dispatcher (UI-thread) exception, an
    /// unhandled exception on any other thread, or a Task fault nobody awaited. None of these
    /// otherwise leave a trace once the process exits.
    /// </summary>
    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
            _logger.LogError(args.Exception, "Unhandled exception on the UI thread.");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger.LogError(args.ExceptionObject as Exception, "Unhandled exception (terminating: {IsTerminating}).", args.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };
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
        ArgumentNullException.ThrowIfNull(e);

        _logger.LogInformation("UAssetEditor exiting (code {ExitCode}).", e.ApplicationExitCode);

        _services?.GetService<MainViewModel>()?.Dispose();
        _services?.Dispose();

        // PakWorkerProcess.Shared owns a live child process (see its own doc comment) and is
        // never disposed by anything upstream of here - without this, the worker only notices
        // its pipe broke (and exits) once the OS gets around to closing this process's handles
        // during teardown, which is neither prompt nor guaranteed to run before the OS itself
        // considers this process gone. Disposing explicitly kills it deterministically instead.
        PakWorkerProcess.Shared.Dispose();

        // Disposing the factory disposes every provider it owns (the file logger included),
        // which flushes and joins its background writer thread so the exit line above actually
        // reaches disk instead of being dropped mid-write.
        _loggerFactory.Dispose();

        base.OnExit(e);
    }
}
