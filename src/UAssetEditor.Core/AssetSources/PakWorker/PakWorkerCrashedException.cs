namespace UAssetEditor.Core.AssetSources.PakWorker;

/// <summary>
/// The pak worker process died mid-operation - either it exited (a native crash inside
/// repak_bind.dll, most notably the confirmed STATUS_STACK_BUFFER_OVERRUN on a specific
/// real-world entry) or its pipe broke unexpectedly. Thrown in place of whatever the failed
/// operation would otherwise have returned; the process itself never takes the app down,
/// this is a perfectly ordinary catchable exception. <see cref="ExitCode"/> is compared
/// against the known repak crash code by callers that want to say something more specific
/// than "the worker exited."
/// </summary>
public sealed class PakWorkerCrashedException : Exception
{
    /// <summary>The Win32/NT exception code the worker process exited with, if known - STATUS_STACK_BUFFER_OVERRUN (0xC0000409) is the one confirmed real-world repro.</summary>
    public int? ExitCode { get; }

    /// <summary>Which entry (or other operation detail) was in flight when the worker died, for status/log text.</summary>
    public string? Operation { get; }

    public const int StatusStackBufferOverrun = unchecked((int)0xC0000409);

    public PakWorkerCrashedException(string message, string? operation, int? exitCode, Exception? inner = null)
        : base(message, inner)
    {
        Operation = operation;
        ExitCode = exitCode;
    }

    public bool IsKnownRepakCrash => ExitCode == StatusStackBufferOverrun;
}
