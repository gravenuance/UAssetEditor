using UAssetEditor.Core.Tests;
using UAssetEditor.Mcp.Tests.Rig;
using UAssetEditor.Mcp.Tools;

namespace UAssetEditor.Mcp.Tests;

public sealed class PathLocksTests : IDisposable
{
    private readonly TestLinks _links = new();
    private readonly TempFolder _folder = new();

    public void Dispose()
    {
        _links.Dispose(); // before the folder: a junction must go as a link, not be walked into
        _folder.Dispose();
    }

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    /// <summary>Runs <paramref name="count"/> tasks that each hold the lock on their paths for a few scheduler turns, and returns the most that were inside at once.</summary>
    private async Task<int> MostInsideAtOnce(int count, Func<int, string[]> pathsOf)
    {
        var inside = 0;
        var most = 0;
        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(() => PathLocks.RunAsync(pathsOf(i), async () =>
        {
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref most, now);
            for (var turn = 0; turn < 5; turn++) await Task.Yield();
            Interlocked.Decrement(ref inside);
            return 0;
        }, _ct), _ct)).ToList();

        await Task.WhenAll(tasks).WaitAsync(Patience, _ct);
        return most;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
        }
    }

    [Fact]
    public async Task CallsOnTheSamePath_RunOneAtATime()
    {
        Assert.Equal(1, await MostInsideAtOnce(20, _ => ["D:/x/A.uasset"]));
    }

    [Fact]
    public async Task CallsOnThePathSpelledWithAnotherCase_RunOneAtATime_WhereThePathsAreCaseInsensitive()
    {
        var most = await MostInsideAtOnce(20, i => [i % 2 == 0 ? "D:/x/A.uasset" : "d:/X/a.UASSET"]);

        if (OperatingSystem.IsWindows()) Assert.Equal(1, most);
    }

    [Fact]
    public async Task CallsOnTwoNamesForOneFile_ThroughAJunction_RunOneAtATime()
    {
        var real = _folder.Combine("real");
        Directory.CreateDirectory(real);
        var alias = _folder.Combine("alias");
        _links.MakeJunction(alias, real);

        var most = await MostInsideAtOnce(20, i => [Path.Combine(i % 2 == 0 ? real : alias, "A.uasset")]);

        Assert.Equal(1, most);
    }

    [Fact]
    public async Task CallsOnDifferentPaths_DoNotWaitForEachOther()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() => PathLocks.RunAsync(["D:/x/A.uasset"], async () =>
        {
            entered.SetResult();
            await release.Task;
            return 0;
        }, _ct), _ct);
        await entered.Task.WaitAsync(Patience, _ct);

        var other = await PathLocks.RunAsync(["D:/x/B.uasset"], () => Task.FromResult(7), _ct).WaitAsync(Patience, _ct);

        Assert.Equal(7, other);
        release.SetResult();
        await holder.WaitAsync(Patience, _ct);
    }

    [Fact]
    public async Task CallsHoldingSeveralPaths_NeverDeadlockWhateverOrderTheyNameThem()
    {
        var most = await MostInsideAtOnce(20, i => i % 2 == 0 ? ["D:/x/A.uasset", "D:/x/B.uasset"] : ["D:/x/B.uasset", "D:/x/A.uasset"]);

        Assert.Equal(1, most);
    }

    [Fact]
    public async Task ACallCancelledWhileWaiting_ThrowsWithoutBlockingTheNext()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() => PathLocks.RunAsync(["D:/x/A.uasset"], async () =>
        {
            entered.SetResult();
            await release.Task;
            return 0;
        }, _ct), _ct);
        await entered.Task.WaitAsync(Patience, _ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var waiting = PathLocks.RunAsync(["D:/x/A.uasset"], () => Task.FromResult(1), cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.SetResult();
        await holder.WaitAsync(Patience, _ct);
        Assert.Equal(3, await PathLocks.RunAsync(["D:/x/A.uasset"], () => Task.FromResult(3), _ct).WaitAsync(Patience, _ct));
    }

    [Fact]
    public async Task AFailingCall_ReleasesItsPaths()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => PathLocks.RunAsync<int>(["D:/x/A.uasset"], () => throw new InvalidOperationException("boom"), _ct));

        Assert.Equal(4, await PathLocks.RunAsync(["D:/x/A.uasset"], () => Task.FromResult(4), _ct).WaitAsync(Patience, _ct));
    }
}
