using System.Text;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.AssetSources.PakWorker;

namespace UAssetEditor.Core.Tests;

/// <summary>
/// Proves the process-isolation actually protects the test host (standing in for the real
/// app) from a worker crash, using a magic "__TEST_CRASH__" entry path the worker
/// deliberately fail-fasts on (see Program.cs) instead of depending on the real,
/// hard-to-reproduce-in-a-unit-test native repak bug.
/// </summary>
[Collection("Pak")]
public class PakWorkerCrashRecoveryTests
{
    [Fact]
    public void ReadOriginalBytes_MagicCrashEntry_ThrowsInsteadOfKillingTheTestHost()
    {
        var pakPath = TestPaks.CreatePak(new Dictionary<string, byte[]>
        {
            ["Foo.uasset"] = Encoding.UTF8.GetBytes("foo"),
        });

        using var source = new PakAssetSource(pakPath);

        // The magic entry fail-fasts the worker instead of returning bytes - proving the
        // exception type itself (not a specific exit code, which varies by runtime/OS for
        // Environment.FailFast - the real STATUS_STACK_BUFFER_OVERRUN code is a property of
        // the actual native crash, exercised separately in the real-pak repro, not here) is
        // what matters: the test host below is still alive to make this assertion at all.
        Assert.Throws<PakWorkerCrashedException>(() => source.ReadOriginalBytes("__TEST_CRASH__"));
    }

    [Fact]
    public void ReadOriginalBytes_AfterACrashedEntry_StillSucceedsForADifferentEntryInTheSamePak()
    {
        var pakPath = TestPaks.CreatePak(new Dictionary<string, byte[]>
        {
            ["Foo.uasset"] = Encoding.UTF8.GetBytes("foo-bytes"),
            ["Bar.uasset"] = Encoding.UTF8.GetBytes("bar-bytes"),
        });

        using var source = new PakAssetSource(pakPath);

        Assert.Throws<PakWorkerCrashedException>(() => source.ReadOriginalBytes("__TEST_CRASH__"));

        // The crashed call triggered a transparent respawn-and-reopen (see
        // PakReaderHandle.ReadEntryAsync) - a completely different entry, read right after,
        // must work normally without the caller reopening the pak by hand.
        using var fooBytes = source.ReadOriginalBytes("Foo.uasset");
        Assert.Equal("foo-bytes", Encoding.UTF8.GetString(fooBytes.Span));

        using var barBytes = source.ReadOriginalBytes("Bar.uasset");
        Assert.Equal("bar-bytes", Encoding.UTF8.GetString(barBytes.Span));
    }

    [Fact]
    public void Unpack_OneEntryCrashesTheWorker_RestOfTheBatchStillSucceeds()
    {
        // Proves PakUnpacker's per-entry catch+continue end to end (not just the reader
        // handle in isolation, above): a bad entry mid-batch is recorded as a failure, and
        // every other entry - both before and after it in iteration order - still lands on
        // disk, rather than the whole Unpack aborting partway through.
        var pakPath = TestPaks.CreatePak(new Dictionary<string, byte[]>
        {
            ["Foo.uasset"] = Encoding.UTF8.GetBytes("foo-bytes"),
            ["__TEST_CRASH__"] = Encoding.UTF8.GetBytes("never seen"),
            ["Bar.uasset"] = Encoding.UTF8.GetBytes("bar-bytes"),
        });
        var destination = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Unpack_" + Guid.NewGuid());

        try
        {
            using var source = new PakAssetSource(pakPath);
            var result = PakUnpacker.Unpack(source, destination, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, result.SucceededCount);
            var failedEntry = Assert.Single(result.FailedEntries);
            Assert.Equal("__TEST_CRASH__", failedEntry.Entry);

            Assert.Equal("foo-bytes", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(destination, "Foo.uasset"))));
            Assert.Equal("bar-bytes", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(destination, "Bar.uasset"))));
            Assert.False(File.Exists(Path.Combine(destination, "__TEST_CRASH__")));
        }
        finally
        {
            File.Delete(pakPath);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }
}
