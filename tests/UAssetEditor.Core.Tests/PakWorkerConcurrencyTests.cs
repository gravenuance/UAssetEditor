using System.Text;
using UAssetAPI;
using UAssetEditor.Core.AssetSources.PakWorker;

namespace UAssetEditor.Core.Tests;

/// <summary>Several callers on one worker at once (the MCP server runs tool calls concurrently), and sessions that outlive a worker restart.</summary>
[Collection("Pak")]
public sealed class PakWorkerConcurrencyTests : IClassFixture<CleanWorkerFolder>, IDisposable
{
    private const int EntrySize = 96 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly PakWorkerProcess _worker;
    private readonly List<string> _files = [];

    public PakWorkerConcurrencyTests(CleanWorkerFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        _worker = new PakWorkerProcess(folder.WorkerExecutable);
    }

    public void Dispose()
    {
        _worker.Dispose();
        foreach (var file in _files) TempPak.DeleteQuietly(file);
    }

    /// <summary>An entry whose every byte is the given value, so a payload mixed up with another entry's shows.</summary>
    private static byte[] Filled(byte value) => Enumerable.Repeat(value, EntrySize).ToArray();

    private string PakWith(params (string Entry, byte[] Bytes)[] entries)
    {
        var path = TestPaks.CreatePak(entries.ToDictionary(e => e.Entry, e => e.Bytes));
        _files.Add(path);
        return path;
    }

    private async Task CrashAsync()
    {
        var crash = new PakWorkerRequest { Opcode = PakWorkerOpcode.ReadEntry, EntryPath = "__TEST_CRASH__" };
        await Assert.ThrowsAsync<PakWorkerCrashedException>(() => new PakWorkerClient(_worker).SendPooledAsync(crash, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ParallelReadsAndAWrite_OnOneWorker_EachGetTheirOwnBytes()
    {
        var pakA = PakWith(("A0.uasset", Filled(0xA0)), ("A1.uasset", Filled(0xA1)));
        var pakB = PakWith(("B0.uasset", Filled(0xB0)), ("B1.uasset", Filled(0xB1)));
        var written = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Concurrent_" + Guid.NewGuid().ToString("N") + ".pak");
        _files.Add(written);

        var readers = new List<(PakReaderHandle Reader, string Entry, byte Value)>();
        foreach (var (pak, entry, value) in new[] { (pakA, "A0.uasset", (byte)0xA0), (pakA, "A1.uasset", (byte)0xA1), (pakB, "B0.uasset", (byte)0xB0), (pakB, "B1.uasset", (byte)0xB1) })
        {
            var reader = new PakReaderHandle(_worker, pak, null);
            await reader.OpenAsync(TestContext.Current.CancellationToken);
            readers.Add((reader, entry, value));
        }

        using var writer = new PakWriterHandle(_worker);
        await writer.OpenAsync(written, "../../../Game/", PakVersion.V11, null, null, TestContext.Current.CancellationToken);

        try
        {
            var tasks = readers.Select(r => Task.Run(async () =>
            {
                for (var i = 0; i < 25; i++)
                {
                    using var bytes = await r.Reader.ReadEntryAsync(r.Entry, TestContext.Current.CancellationToken);
                    Assert.Equal(EntrySize, bytes.Length);
                    Assert.True(bytes.Span.IndexOfAnyExcept(r.Value) < 0, $"{r.Entry} came back with another entry's bytes");
                }
            }, TestContext.Current.CancellationToken)).ToList();
            tasks.Add(Task.Run(async () =>
            {
                for (var i = 0; i < 25; i++)
                    await writer.WriteFileAsync($"W{i}.uasset", Filled((byte)i), TestContext.Current.CancellationToken);
                await writer.WriteIndexAsync(TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken));

            await Task.WhenAll(tasks).WaitAsync(Timeout, TestContext.Current.CancellationToken); // interleaved frames can leave a caller waiting forever
        }
        finally
        {
            foreach (var (reader, _, _) in readers) reader.Dispose();
        }
    }

    [Fact]
    public async Task Sessions_AfterTheWorkerRestartedForSomeoneElse_NeverAddressAnotherHandlesSession()
    {
        var pakA = PakWith(("Foo.uasset", Encoding.UTF8.GetBytes("A-bytes")));
        var pakB = PakWith(("Foo.uasset", Encoding.UTF8.GetBytes("B-bytes")));
        using var first = new PakReaderHandle(_worker, pakA, null);
        await first.OpenAsync(TestContext.Current.CancellationToken);
        using var second = new PakReaderHandle(_worker, pakB, null);

        await CrashAsync();
        await second.OpenAsync(TestContext.Current.CancellationToken); // opens on the restarted worker; first never heard of the crash

        using (var bytes = await first.ReadEntryAsync("Foo.uasset", TestContext.Current.CancellationToken))
        {
            Assert.Equal("A-bytes", Encoding.UTF8.GetString(bytes.Span));
        }

        first.Dispose(); // must not close the second reader's session
        using var secondBytes = await second.ReadEntryAsync("Foo.uasset", TestContext.Current.CancellationToken);
        Assert.Equal("B-bytes", Encoding.UTF8.GetString(secondBytes.Span));
    }

    [Fact]
    public async Task Writer_AfterTheWorkerRestarted_FailsClearlyInsteadOfWritingToAnotherSession()
    {
        var pak = PakWith(("Foo.uasset", Encoding.UTF8.GetBytes("foo")));
        var written = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Lost_" + Guid.NewGuid().ToString("N") + ".pak");
        _files.Add(written);
        using var writer = new PakWriterHandle(_worker);
        await writer.OpenAsync(written, "../../../Game/", PakVersion.V11, null, null, TestContext.Current.CancellationToken);
        await CrashAsync();
        using var reader = new PakReaderHandle(_worker, pak, null);
        await reader.OpenAsync(TestContext.Current.CancellationToken); // a session on the new worker, which the writer's old id must not reach

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteFileAsync("Bar.uasset", new byte[] { 1 }, TestContext.Current.CancellationToken));

        Assert.Equal("The pak worker restarted while this pak was being written, so the new pak is incomplete.", ex.Message);
    }

    [Fact]
    public async Task ParallelReadsOnOneHandle_WhileItMovesToAnotherWorker_AllSeeTheSameOutcome()
    {
        var dll = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_NoOodle_" + Guid.NewGuid().ToString("N"), "oo2core_9_win64.dll");
        var pak = OodlePaks.CreateOodleMarked();
        _files.Add(pak);
        using var reader = new PakReaderHandle(_worker, pak, null, _ => new OodleLookup(dll));
        await reader.OpenAsync(TestContext.Current.CancellationToken);

        var reads = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadEntryAsync("Foo.uasset", TestContext.Current.CancellationToken));
            return ex.Message;
        }, TestContext.Current.CancellationToken)).ToList();

        var messages = await Task.WhenAll(reads).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.All(messages, m => Assert.Equal($"Could not load {dll}. Use a 64-bit oo2core_*_win64.dll.", m));
    }

    [Fact]
    public async Task ForOodleLibrary_AndEnsure_AfterDispose_Throw()
    {
        var dll = Path.Combine(Path.GetTempPath(), "oo2core_9_win64.dll");
        _worker.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _worker.ForOodleLibrary(dll));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _worker.EnsureAsync(TestContext.Current.CancellationToken));
    }
}
