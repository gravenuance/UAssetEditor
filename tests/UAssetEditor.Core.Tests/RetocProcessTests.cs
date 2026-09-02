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
    public async Task ConvertToLegacyAsync_PropagatesRetocsOwnFailureReason()
    {
        // A container built from plain (non-package) files - confirmed empirically - has no
        // script objects, which retoc always expects to convert back to legacy format, even
        // for an otherwise-empty container. That's a real, reproducible retoc failure (not a
        // gap in this test's own setup), so this validates IoStoreConversionException actually
        // carries retoc's real stderr text through, the same way the missing-file test below
        // validates the same plumbing for a different failure.
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

            Assert.Equal(1, exception.ExitCode);
            Assert.Contains("ScriptObjects", exception.Message, StringComparison.Ordinal);
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
