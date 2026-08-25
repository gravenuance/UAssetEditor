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

    public IoStoreConversionException()
    {
    }

    public IoStoreConversionException(string message)
        : base(message)
    {
    }

    public IoStoreConversionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public IoStoreConversionException(string message, int exitCode)
        : base(message)
    {
        ExitCode = exitCode;
    }
}
