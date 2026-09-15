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
/// (confirmed empirically - it has no script objects, which a real cooked container always
/// would), so <see cref="ConvertToLegacyAsync_OnRealContainer_DoesNotThrow"/> only proves the
/// call succeeds and produces well-formed output, not that any particular asset comes back.
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
    public async Task ConvertToLegacyAsync_OnRealContainer_DoesNotThrow()
    {
        // Real-world repro (from the app's own log file): a small, standalone container (e.g.
        // a single-mod .utoc) has no ScriptObjects chunk of its own - only the game's full,
        // global container normally carries one - so to-legacy's unconditional attempt to also
        // extract script objects used to hard-fail the *entire* conversion, even though the
        // actually-requested assets converted fine on their own. ConvertToLegacyAsync now
        // always passes --no-script-objects (this app has no feature that reads that output
        // anyway) to skip exactly that step. A container built from plain (non-package) files
        // is the same shape - confirmed empirically, it has no script objects either - so this
        // proves the fix without needing real cooked Unreal Engine content.
        var workDir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Retoc_" + Guid.NewGuid());
        Directory.CreateDirectory(workDir);
        try
        {
            var pakPath = BuildLegacyTestPak(workDir);
            var utocPath = Path.Combine(workDir, "test.utoc");
            await RetocProcess.ConvertToZenAsync(pakPath, utocPath, "UE5_3", aesKey: null, cancellationToken: TestContext.Current.CancellationToken);

            // No output-directory assertion: with empty filters and a container that (like a
            // real single-mod .utoc) has no convertible legacy packages of its own, retoc
            // legitimately extracts zero assets and never creates the output folder at all -
            // confirmed by running retoc.exe directly against this exact fixture. The fix under
            // test is that this call no longer throws at all (it used to, on the ScriptObjects
            // chunk, before --no-script-objects), not what ends up on disk.
            var outputDir = Path.Combine(workDir, "legacy_out");
            var exception = await Record.ExceptionAsync(() =>
                RetocProcess.ConvertToLegacyAsync(utocPath, outputDir, filters: [], aesKey: null, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Null(exception);
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
