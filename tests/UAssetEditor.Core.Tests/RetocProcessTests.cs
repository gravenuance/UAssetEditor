using System.Linq;
using System.Text;
using UAssetAPI;
using UAssetEditor.Core.AssetSources.IoStore;

namespace UAssetEditor.Core.Tests;

/// <summary>
/// Exercises <see cref="RetocProcess"/> against the real vendored retoc.exe (see
/// EmbeddedToolLocator's dev fallback - finds src/UAssetEditor.App/vendor/retoc.exe directly,
/// same as PakWorkerProcess finds its own dev build). What's testable without real cooked
/// Unreal Engine content is the plumbing - process invocation, argument handling, output
/// parsing, error surfacing - not a full asset-level round trip: a container built from plain
/// (non-package) files converts to Zen fine but has nothing "legacy" to extract back out
/// (confirmed empirically - it has no script objects or convertible packages, either of which a
/// real cooked container always would), which is exactly the "0 succeeded" shape
/// <see cref="ConvertToLegacyAsync_WhenNoAssetsSucceed_ThrowsWithRetocsOwnDiagnostics"/> tests.
/// </summary>
[Collection("Pak")]
public class RetocProcessTests
{
    private static string BuildLegacyTestPak(string workDir)
    {
        var pakPath = Path.Combine(workDir, "test_P.pak");
        using (var stream = File.Create(pakPath))
        using (var builder = new PakBuilder())
        using (var writer = builder.Writer(stream, PakVersion.V11, "../../../Game/", 0))
        {
            writer.WriteFile("Content/Foo.txt", Encoding.UTF8.GetBytes("hello foo"));
            writer.WriteIndex();
        }
        return pakPath;
    }

    [Fact]
    public async Task ConvertToZenAsync_OnALegacyPak_ProducesAUtocAndUcas()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Retoc_" + Guid.NewGuid());
        Directory.CreateDirectory(workDir);
        try
        {
            var pakPath = BuildLegacyTestPak(workDir);
            var utocPath = Path.Combine(workDir, "test.utoc");

            await RetocProcess.ConvertToZenAsync(pakPath, utocPath, "UE5_3", aesKey: null, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(File.Exists(utocPath));
            Assert.True(File.Exists(Path.ChangeExtension(utocPath, ".ucas")));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ListAsync_OnARealContainer_DoesNotThrow()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Retoc_" + Guid.NewGuid());
        Directory.CreateDirectory(workDir);
        try
        {
            var pakPath = BuildLegacyTestPak(workDir);
            var utocPath = Path.Combine(workDir, "test.utoc");
            await RetocProcess.ConvertToZenAsync(pakPath, utocPath, "UE5_3", aesKey: null, cancellationToken: TestContext.Current.CancellationToken);

            var entries = await RetocProcess.ListAsync(utocPath, aesKey: null, cancellationToken: TestContext.Current.CancellationToken);

            // Not asserting on specific paths: a container built from plain non-package files
            // has nothing but its own ContainerHeader chunk, which ListAsync deliberately
            // omits (see its own doc comment - a chunk with no real per-package path isn't
            // useful to show in a tree of "things you can convert").
            Assert.NotNull(entries);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ConvertToLegacyAsync_WithFiltersTooLongForOneCommandLine_BatchesInsteadOfFailingToStartTheProcess()
    {
        // Real-world repro (from the app's own log file): converting a large checked selection
        // (hundreds of long asset paths as -f filters) threw Win32Exception(206) "The filename
        // or extension is too long" trying to start retoc.exe at all - Windows' CreateProcess
        // has a hard ~32,767-character command-line limit. Enough fake filters here to
        // comfortably exceed that limit (~90,000 chars total) proves the real vendored
        // retoc.exe process actually starts via multiple batched calls instead of one
        // oversized one - a Win32Exception is a different exception type entirely, so
        // ThrowsAsync<IoStoreConversionException> below only passes if retoc itself actually
        // ran (repeatedly) and reported back, not if the process failed to start at all.
        var workDir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Retoc_" + Guid.NewGuid());
        Directory.CreateDirectory(workDir);
        try
        {
            var pakPath = BuildLegacyTestPak(workDir);
            var utocPath = Path.Combine(workDir, "test.utoc");
            await RetocProcess.ConvertToZenAsync(pakPath, utocPath, "UE5_3", aesKey: null, cancellationToken: TestContext.Current.CancellationToken);

            var filters = Enumerable.Range(0, 1000)
                .Select(i => $"../../../Marvel/Content/Marvel/Characters/9999/FakeAssetNameForBatchingTest_{i:D4}.uasset")
                .ToList();

            var outputDir = Path.Combine(workDir, "legacy_out");
            var exception = await Assert.ThrowsAsync<IoStoreConversionException>(() =>
                RetocProcess.ConvertToLegacyAsync(utocPath, outputDir, filters, aesKey: null, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal("No assets converted - see the log for retoc's reason.", exception.UserMessage);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ConvertToLegacyAsync_WithTooManyFiltersForAPakOutput_RefusesRatherThanTruncatingSilently()
    {
        // retoc truncates its .pak output fresh on every invocation, unlike a loose-folder
        // output (which only ever adds/overwrites individual files) - so batching a filter
        // list too large for one command line would silently keep only the last batch's
        // entries for a .pak target. This must refuse outright, before even attempting a
        // single retoc call (no real .utoc needed to prove that: the same filter list that
        // batches safely for a loose-folder output - see the sibling test above - must be
        // rejected here purely because the output extension is ".pak").
        var filters = Enumerable.Range(0, 1000)
            .Select(i => $"../../../Marvel/Content/Marvel/Characters/9999/FakeAssetNameForBatchingTest_{i:D4}.uasset")
            .ToList();

        var exception = await Assert.ThrowsAsync<IoStoreConversionException>(() =>
            RetocProcess.ConvertToLegacyAsync("nonexistent.utoc", "output.pak", filters, aesKey: null, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("Selection too large for one .pak - convert to a loose folder instead.", exception.UserMessage);
    }

    [Fact]
    public async Task ConvertToLegacyAsync_WhenNoAssetsSucceed_ThrowsWithRetocsOwnDiagnostics()
    {
        // Real-world repro (from the app's own log file, on a real game mod's small, standalone
        // .utoc): to-legacy can exit 0 having converted zero assets - retoc logs each per-package
        // failure (or, as here, simply finds no convertible packages at all) at "info" level and
        // keeps going rather than failing the whole process. That used to look exactly like
        // success to this app - no exception, an empty output folder, nothing in the log - since
        // ConvertToLegacyAsync discarded retoc's stdout entirely. It now surfaces "0 succeeded" as
        // a real failure, with retoc's own diagnostic line(s) carried through to the log. A
        // container built from plain (non-package) files reproduces "0 succeeded" without needing
        // real cooked Unreal Engine content (it also has no script objects - see
        // ConvertToZenAsync_OnALegacyPak_ProducesAUtocAndUcas's sibling tests).
        var workDir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Retoc_" + Guid.NewGuid());
        Directory.CreateDirectory(workDir);
        try
        {
            var pakPath = BuildLegacyTestPak(workDir);
            var utocPath = Path.Combine(workDir, "test.utoc");
            await RetocProcess.ConvertToZenAsync(pakPath, utocPath, "UE5_3", aesKey: null, cancellationToken: TestContext.Current.CancellationToken);

            var outputDir = Path.Combine(workDir, "legacy_out");
            var exception = await Assert.ThrowsAsync<IoStoreConversionException>(() =>
                RetocProcess.ConvertToLegacyAsync(utocPath, outputDir, filters: [], aesKey: null, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal("No assets converted - see the log for retoc's reason.", exception.UserMessage);
            Assert.Contains("legacy assets", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ConvertToZenAsync_WhenCanceled_KillsRetocInsteadOfLettingItFinish()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Retoc_" + Guid.NewGuid());
        Directory.CreateDirectory(workDir);
        try
        {
            var pakPath = BuildLegacyTestPak(workDir);
            var utocPath = Path.Combine(workDir, "test.utoc");

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RetocProcess.ConvertToZenAsync(pakPath, utocPath, "UE5_3", aesKey: null, cancellationToken: cts.Token));

            // A generous window for retoc to finish on its own if it wasn't actually killed -
            // without killing the child process, cancellation only stopped this method from
            // waiting, not the conversion itself, so the "canceled" output would still appear.
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            Assert.False(File.Exists(utocPath));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ListAsync_WithAnAesKey_DoesNotRejectItAsAnUnexpectedArgument()
    {
        // Real-world repro (from the app's own log file): retoc's --aes-key is a *global*
        // option, only recognized before the subcommand ("retoc.exe --aes-key <KEY> list ..."),
        // not after it like every other per-subcommand flag - passing one alongside an AES key
        // used to fail every single retoc call (list, to-zen, to-legacy alike) with "unexpected
        // argument '--aes-key' found", not just when the key material was actually wrong.
        var workDir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Retoc_" + Guid.NewGuid());
        Directory.CreateDirectory(workDir);
        try
        {
            var pakPath = BuildLegacyTestPak(workDir);
            var utocPath = Path.Combine(workDir, "test.utoc");
            await RetocProcess.ConvertToZenAsync(pakPath, utocPath, "UE5_3", aesKey: null, cancellationToken: TestContext.Current.CancellationToken);

            var aesKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            var entries = await RetocProcess.ListAsync(utocPath, aesKey, cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotNull(entries);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ListAsync_OnMissingFile_ThrowsWithRealRetocErrorText()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Retoc_Missing_" + Guid.NewGuid() + ".utoc");

        var exception = await Assert.ThrowsAsync<IoStoreConversionException>(() =>
            RetocProcess.ListAsync(missingPath, aesKey: null, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, exception.ExitCode);
        Assert.Contains(missingPath, exception.Message, StringComparison.Ordinal);
    }
}
