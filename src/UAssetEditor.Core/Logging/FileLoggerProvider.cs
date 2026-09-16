using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace UAssetEditor.Core.Logging;

/// <summary>
/// Writes every log entry to a rotating (one file per day) plain-text log under
/// <paramref name="directory"/>, so a crash or a hard-to-reproduce bug leaves something to hand
/// over after the process is gone - the in-app StatusMessage text disappears the moment the
/// window closes. All categories share one file; a single background writer thread drains a
/// queue so logging calls on the UI thread never block on file I/O.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly BlockingCollection<string> _queue = new();
    private readonly Thread _writerThread;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    public FileLoggerProvider(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _directory = directory;
        Directory.CreateDirectory(_directory);

        _writerThread = new Thread(WriteLoop) { IsBackground = true, Name = "FileLoggerProvider" };
        _writerThread.Start();
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(name, Enqueue));

    private void Enqueue(string line)
    {
        if (!_disposed)
            try { _queue.Add(line); } catch (InvalidOperationException) { /* queue completed during shutdown */ }
    }

    /// <summary>Opens a writer for <paramref name="path"/>, or null if another process holds the file
    /// (e.g. a second instance) or a transient disk issue prevents it - never throws.</summary>
    private static StreamWriter? TryOpenWriter(string path)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            return new StreamWriter(stream, Encoding.UTF8);
        }
        catch (IOException)
        {
            stream?.Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            stream?.Dispose();
            return null;
        }
    }

    private void WriteLoop()
    {
        // One file per calendar day - reopened whenever the date rolls over mid-run, so a
        // long-lived session doesn't keep appending to yesterday's file forever.
        string? currentPath = null;
        StreamWriter? writer = null;
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                var path = Path.Combine(_directory, $"log-{DateTime.UtcNow:yyyyMMdd}.txt");
                if (path != currentPath || writer == null)
                {
                    writer?.Dispose();
                    writer = TryOpenWriter(path);
                    currentPath = writer != null ? path : null;
                }

                // Couldn't open the file this time (locked/denied) - drop the line, retry next one.
                if (writer == null) continue;

                try
                {
                    writer.WriteLine(line);
                    writer.Flush();
                }
                catch (IOException)
                {
                    writer.Dispose();
                    writer = null;
                    currentPath = null;
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _queue.CompleteAdding();
        _writerThread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }
}

/// <summary>Formats and hands off one category's log lines to <see cref="FileLoggerProvider"/>'s shared writer queue.</summary>
internal sealed class FileLogger(string category, Action<string> write) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {category}: {message}";
        if (exception != null)
            line += Environment.NewLine + exception;

        write(line);
    }
}
