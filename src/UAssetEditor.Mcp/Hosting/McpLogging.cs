using Microsoft.Extensions.Logging;
using UAssetEditor.Core;
using UAssetEditor.Core.Logging;
using UAssetEditor.Mcp.Tools;

namespace UAssetEditor.Mcp.Hosting;

/// <summary>Where the server logs. Standard output carries the protocol, so diagnostics go to a file under the cache
/// folder's <c>Logs</c>, in a subfolder of its own so the desktop app's log of the same day is never contended.</summary>
internal static class McpLogging
{
    public const string LevelVariable = "UASSETEDITOR_MCP_LOG_LEVEL";

    /// <summary>Daily files older than this are deleted at startup, so the folder cannot grow without limit.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    public static string LogDirectory(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.LogsDirectory, "Mcp");
    }

    /// <summary>Parses a level name; an unknown or empty value means Information, the file logger's lowest level.</summary>
    public static LogLevel ParseLevel(string? name) =>
        Enum.TryParse<LogLevel>(name, ignoreCase: true, out var level) && Enum.IsDefined(level) ? level : LogLevel.Information;

    public static void Configure(ILoggingBuilder logging, AppPaths paths, LogLevel level)
    {
        ArgumentNullException.ThrowIfNull(logging);

        logging.ClearProviders();
#pragma warning disable CA2000 // The logger factory owns the provider and disposes it with the host.
        logging.AddProvider(new FileLoggerProvider(LogDirectory(paths)));
#pragma warning restore CA2000
        logging.SetMinimumLevel(level);
        logging.AddFilter("Microsoft", LogLevel.Warning);
        logging.AddFilter("System", LogLevel.Warning);
        logging.AddFilter("ModelContextProtocol", LogLevel.Warning);
    }

    /// <summary>Deletes daily log files last written more than <see cref="Retention"/> before <paramref name="now"/>; a file in use is left alone.</summary>
    public static int PruneOldLogs(string directory, DateTimeOffset now)
    {
        if (!Directory.Exists(directory)) return 0;

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "log-*.txt"))
        {
            if (now - File.GetLastWriteTimeUtc(file) <= Retention) continue;
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still open or read-only: the next start tries again.
            }
        }
        return deleted;
    }

    /// <summary>Writes a last record for an exception nobody caught, and notes unobserved task faults instead of losing them.</summary>
    public static void HandleProcessWideFailures(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogFailure(log, LogLevel.Critical, "Unhandled exception; the process is exiting", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogFailure(log, LogLevel.Error, "Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Logs <paramref name="exception"/> with its stack as text, redacted, so no layer's message can carry an AES key into the log.</summary>
    public static void LogFailure(ILogger log, LogLevel level, string what, Exception? exception)
    {
        ArgumentNullException.ThrowIfNull(log);
        log.Log(level, "{What}{NewLine}{Exception}", what, Environment.NewLine, Secrets.Redact(exception?.ToString()));
    }
}
