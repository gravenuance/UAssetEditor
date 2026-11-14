namespace UAssetEditor.Core;

/// <summary>
/// Where the app keeps what it can rebuild: logs and the native tools it extracts. The root is
/// <c>UASSETEDITOR_CACHE_DIR</c> when set, else <c>%LOCALAPPDATA%\UAssetEditor</c> on Windows or
/// <c>$XDG_CACHE_HOME/UAssetEditor</c> (<c>~/.cache/UAssetEditor</c>) elsewhere. The usmap store has its own
/// variable (<see cref="Games.MappingsStore"/>) and the last-session file stays in the OS local folder.
/// </summary>
public sealed class AppPaths
{
    public const string CacheDirectoryVariable = "UASSETEDITOR_CACHE_DIR";

    private const string AppFolderName = "UAssetEditor";

    public AppPaths(Func<string, string?> getEnvironmentVariable, string defaultCacheRoot)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultCacheRoot);

        var fromEnvironment = getEnvironmentVariable(CacheDirectoryVariable);
        CacheRoot = Path.GetFullPath(!string.IsNullOrWhiteSpace(fromEnvironment) ? fromEnvironment : defaultCacheRoot);
    }

    /// <summary>The process-wide paths: the real environment and the OS default for this platform.</summary>
    public static AppPaths Current { get; } = new(Environment.GetEnvironmentVariable, DefaultCacheRoot());

    public string CacheRoot { get; }

    public string LogsDirectory => Path.Combine(CacheRoot, "Logs");

    public string ToolsDirectory => Path.Combine(CacheRoot, "Tools");

    private static string DefaultCacheRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

        var xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var cacheHome = !string.IsNullOrWhiteSpace(xdgCache)
            ? xdgCache
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(cacheHome, AppFolderName);
    }
}
