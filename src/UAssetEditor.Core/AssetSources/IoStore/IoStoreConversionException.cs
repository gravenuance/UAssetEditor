namespace UAssetEditor.Core.AssetSources.IoStore;

/// <summary>
/// A <see cref="RetocProcess"/> invocation exited with a nonzero code. Unlike
/// <see cref="PakWorker.PakWorkerCrashedException"/>, there's no respawn-and-retry story here -
/// retoc is a one-shot CLI, not a persistent session, so a failed conversion just fails; the
/// caller decides whether to let the user retry.
/// </summary>
public sealed class IoStoreConversionException : Exception
{
    /// <summary>The retoc process's exit code.</summary>
    public int ExitCode { get; }

    /// <summary>
    /// A short, human-readable summary of the failure (typically just retoc's own first stderr
    /// line, e.g. "error: unexpected argument '--aes-key' found") - fit for a UI status line,
    /// unlike <see cref="Exception.Message"/> here, which carries the full command and stderr
    /// dump for the log file. Falls back to <see cref="Exception.Message"/> itself for the
    /// standard exception constructors below, which no code in this codebase actually throws
    /// with (kept only to satisfy CA1032).
    /// </summary>
    public string UserMessage { get; }

    public IoStoreConversionException()
    {
        UserMessage = "";
    }

    public IoStoreConversionException(string message)
        : base(message)
    {
        UserMessage = message;
    }

    public IoStoreConversionException(string message, Exception innerException)
        : base(message, innerException)
    {
        UserMessage = message;
    }

    public IoStoreConversionException(string message, int exitCode)
        : base(message)
    {
        ExitCode = exitCode;
        UserMessage = message;
    }

    public IoStoreConversionException(string message, string userMessage, int exitCode)
        : base(message)
    {
        ExitCode = exitCode;
        UserMessage = userMessage;
    }

    /// <summary>The short summary to show in the UI: <see cref="UserMessage"/> for this type, <see cref="Exception.Message"/> (already short for a normal .NET exception) for anything else.</summary>
    public static string Summarize(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex is IoStoreConversionException io ? io.UserMessage : ex.Message;
    }
}
