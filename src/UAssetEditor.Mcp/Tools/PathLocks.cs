using UAssetEditor.Core;

namespace UAssetEditor.Mcp.Tools;

/// <summary>One asynchronous lock per file path for the whole process, so two calls never open, edit and save the same
/// asset at once. The server answers requests concurrently; without this, two saves of one file interleave their writes
/// and their backups.</summary>
internal static class PathLocks
{
    private sealed class Gate : IDisposable
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }

        public void Dispose() => Semaphore.Dispose();
    }

    private static readonly StringComparer KeyComparison =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // An entry lives only while a call holds or waits for it, so the table does not grow with every path ever touched.
    private static readonly Dictionary<string, Gate> Gates = new(KeyComparison);

    /// <summary>Runs <paramref name="work"/> once every path in <paramref name="paths"/> is free, holding them all until it ends.</summary>
    /// <remarks>Paths are taken in one fixed order, so two calls naming the same paths in different orders cannot deadlock.</remarks>
    public static async Task<T> RunAsync<T>(IEnumerable<string> paths, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(work);

        // Resolved, so two names for one file (through a junction or an 8.3 name) share one lock.
        var keys = paths.Select(FinalPaths.Resolve).Distinct(KeyComparison).Order(KeyComparison).ToList();
        var interested = new List<(string Key, Gate Gate)>();
        var acquired = new List<Gate>();
        try
        {
            foreach (var key in keys)
            {
                var gate = Enter(key);
                interested.Add((key, gate));
                await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired.Add(gate);
            }

            return await work().ConfigureAwait(false);
        }
        finally
        {
            foreach (var gate in acquired) gate.Semaphore.Release();
            foreach (var (key, gate) in interested) Leave(key, gate);
        }
    }

    private static Gate Enter(string key)
    {
        lock (Gates)
        {
            if (!Gates.TryGetValue(key, out var gate)) Gates[key] = gate = new Gate();
            gate.Users++;
            return gate;
        }
    }

    private static void Leave(string key, Gate gate)
    {
        lock (Gates)
        {
            if (--gate.Users > 0) return;
            Gates.Remove(key);
            gate.Dispose();
        }
    }
}
