using System.Diagnostics;
using UAssetEditor.Core.AssetSources.PakWorker;

namespace UAssetEditor.Core.Tests;

/// <summary>A host that crashes or is killed takes its pak workers with it: the OS closes the host's job handle, which
/// closing a test-owned job stands in for.</summary>
[Collection("Pak")]
public sealed class PakWorkerJobTests(CleanWorkerFolder folder) : IClassFixture<CleanWorkerFolder>
{
    [Fact]
    public async Task AWorker_EndsWhenItsHostsJobCloses_ThoughItsPipeIsStillOpen()
    {
        using var job = new KillOnCloseJob();
        using var worker = new PakWorkerProcess(folder.WorkerExecutable, job);
        var (processId, _) = await PakWorkerOodleTests.StateAsync(worker);
        using var process = Process.GetProcessById(processId);
        Assert.True(job.Contains(process), $"pak worker {processId} is not in its host's job");

        job.Dispose();

        using var ended = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var exited = await process.WaitForExitAsync(ended.Token).ContinueWith(task => task.IsCompletedSuccessfully, TaskScheduler.Default);
        Assert.True(exited, $"pak worker {processId} was still running 10 s after its job closed");
    }
}
