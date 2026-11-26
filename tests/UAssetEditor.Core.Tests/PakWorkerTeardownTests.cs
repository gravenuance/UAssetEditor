using System.Diagnostics;
using System.Text;
using UAssetEditor.Core.AssetSources.PakWorker;

namespace UAssetEditor.Core.Tests;

/// <summary>What disposing a worker leaves behind: no running worker, so nothing it loaded (repak_bind.dll) stays locked.</summary>
[Collection("Pak")]
public sealed class PakWorkerTeardownTests
{
    /// <summary>Whether the process handle is signalled. Not HasExited: an exit code is set as soon as a kill starts, before the process has ended.</summary>
    private static bool HasEnded(Process process) => process.WaitForExit(0);

    [Fact]
    public async Task Dispose_ReturnsOnlyOnceEveryWorkerHasExited_SoItsFolderCanBeDeleted()
    {
        var dll = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_NoOodle_" + Guid.NewGuid().ToString("N"), "oo2core_9_win64.dll");
        using var plain = TestPaks.CreateTemp(new Dictionary<string, byte[]> { ["Foo.uasset"] = Encoding.UTF8.GetBytes("foo") });
        using var folder = new CleanWorkerFolder();
        var running = new List<Process>();
        try
        {
            using (var worker = new PakWorkerProcess(folder.WorkerExecutable))
            {
                // An open loads repak_bind.dll: once in the default worker, once in a worker of its own for another Oodle DLL.
                foreach (var target in new[] { worker, worker.ForOodleLibrary(dll) })
                {
                    using var reader = new PakReaderHandle(target, plain.Path, null);
                    await reader.OpenAsync(TestContext.Current.CancellationToken);
                }

                // Held open before Dispose, so a process id reused after the exit cannot stand in for the worker.
                running.Add(Process.GetProcessById((await PakWorkerOodleTests.StateAsync(worker)).ProcessId));
                running.Add(Process.GetProcessById((await PakWorkerOodleTests.StateAsync(worker.ForOodleLibrary(dll))).ProcessId));
            }

            var ended = running.Select(HasEnded).ToList();
            ReleaseAll(running);

            Assert.All(ended, e => Assert.True(e, "a worker was still ending when Dispose returned"));
            folder.Delete(); // throws UnauthorizedAccessException while a worker still maps repak_bind.dll
        }
        finally
        {
            ReleaseAll(running);
        }
    }

    /// <summary>Closes the handles: an open process handle keeps the exited process's exe locked too.</summary>
    private static void ReleaseAll(List<Process> processes)
    {
        foreach (var process in processes) process.Dispose();
        processes.Clear();
    }

    [Fact]
    public async Task Dispose_RightAfterAWorkerCrashed_ReturnsOnlyOnceItHasExited()
    {
        using var folder = new CleanWorkerFolder();
        using var plain = TestPaks.CreateTemp(new Dictionary<string, byte[]> { ["Foo.uasset"] = Encoding.UTF8.GetBytes("foo") });
        var crashed = new List<Process>();
        try
        {
            using (var worker = new PakWorkerProcess(folder.WorkerExecutable))
            {
                using (var reader = new PakReaderHandle(worker, plain.Path, null))
                {
                    await reader.OpenAsync(TestContext.Current.CancellationToken);
                }

                crashed.Add(Process.GetProcessById((await PakWorkerOodleTests.StateAsync(worker)).ProcessId));
                var crash = new PakWorkerRequest { Opcode = PakWorkerOpcode.ReadEntry, EntryPath = "__TEST_CRASH__" };
                await Assert.ThrowsAsync<PakWorkerCrashedException>(() => new PakWorkerClient(worker).SendPooledAsync(crash, cancellationToken: TestContext.Current.CancellationToken));
            }

            var ended = HasEnded(crashed[0]);
            ReleaseAll(crashed);

            Assert.True(ended, "the crashed worker was still ending when Dispose returned");
            folder.Delete(); // throws UnauthorizedAccessException while the crashed worker still maps repak_bind.dll
        }
        finally
        {
            ReleaseAll(crashed);
        }
    }

    [Fact]
    public async Task Restart_AfterACrash_WaitsForTheCrashedWorkerToEnd()
    {
        using var folder = new CleanWorkerFolder();
        using var plain = TestPaks.CreateTemp(new Dictionary<string, byte[]> { ["Foo.uasset"] = Encoding.UTF8.GetBytes("foo") });
        using var worker = new PakWorkerProcess(folder.WorkerExecutable);
        using (var reader = new PakReaderHandle(worker, plain.Path, null))
        {
            await reader.OpenAsync(TestContext.Current.CancellationToken);
        }

        using var crashed = Process.GetProcessById((await PakWorkerOodleTests.StateAsync(worker)).ProcessId);
        var crash = new PakWorkerRequest { Opcode = PakWorkerOpcode.ReadEntry, EntryPath = "__TEST_CRASH__" };
        await Assert.ThrowsAsync<PakWorkerCrashedException>(() => new PakWorkerClient(worker).SendPooledAsync(crash, cancellationToken: TestContext.Current.CancellationToken));

        var restarted = (await PakWorkerOodleTests.StateAsync(worker)).ProcessId; // the restart, which replaces the crashed worker

        Assert.NotEqual(crashed.Id, restarted);
        Assert.True(HasEnded(crashed), "the crashed worker was still ending when its replacement started");
    }
}
