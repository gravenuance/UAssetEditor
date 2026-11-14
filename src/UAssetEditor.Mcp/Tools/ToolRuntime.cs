using ModelContextProtocol;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Process-wide rules for the tools that start the bundled helpers.</summary>
internal static class ToolRuntime
{
    // Every pak call shares one worker process and one pipe, which cannot interleave two requests.
    private static readonly SemaphoreSlim PakWorkerGate = new(1, 1);

    /// <summary>The pak worker, retoc and Oodle are Windows builds; the asset tools do not need them.</summary>
    public static void RequireWindows(string tool)
    {
        if (!OperatingSystem.IsWindows())
            throw new McpException($"{tool} needs Windows: the pak worker, retoc and Oodle it relies on are Windows-only.");
    }

    /// <summary>Runs <paramref name="work"/> while holding the pak worker, one call at a time.</summary>
    public static async Task<T> UsePakWorkerAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        await PakWorkerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            PakWorkerGate.Release();
        }
    }

    /// <summary>Runs a blocking Core pak call on a pool thread while holding the pak worker.</summary>
    public static Task<T> UsePakWorkerBlockingAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        return UsePakWorkerAsync(() => Task.Run(work, cancellationToken), cancellationToken);
    }
}
