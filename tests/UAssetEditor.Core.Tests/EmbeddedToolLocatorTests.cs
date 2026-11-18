using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.Core.Tests;

public class EmbeddedToolLocatorTests
{
    [Fact]
    public void ExtractionDirectory_IsUnderTheCacheRootsToolsFolder_PerToolAndBuild()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Tools_Root"));
        var paths = new AppPaths(name => name == AppPaths.CacheDirectoryVariable ? root : null, root + "_unused");

        var directory = EmbeddedToolLocator.ExtractionDirectory(paths, "retoc.exe", "0123456789abcdef");

        Assert.Equal(Path.Combine(root, "Tools", "retoc.exe", "0123456789abcdef"), directory);
    }

    [Fact]
    public void TryResolve_ReturnsNull_WhenNoOverrideEmbeddedCopyOrFallbackHasTheTool()
    {
        var result = EmbeddedToolLocator.TryResolve("uae-test-absent-tool.dll", "UAE_TEST_ABSENT_TOOL_PATH", () => null);

        Assert.Null(result);
    }

    [Fact]
    public void TryResolve_UsesTheDevFallback_WhenNothingIsEmbedded()
    {
        var result = EmbeddedToolLocator.TryResolve("uae-test-fallback-tool.dll", "UAE_TEST_FALLBACK_TOOL_PATH", () => "fallback-path");

        Assert.Equal("fallback-path", result);
    }

    [Fact]
    public void TryResolve_PrefersAnExistingEnvOverride_OverTheFallback()
    {
        const string variable = "UAE_TEST_OVERRIDE_TOOL_PATH";
        var file = Path.Combine(Path.GetTempPath(), "uae-test-override-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllBytes(file, [0]);
        Environment.SetEnvironmentVariable(variable, file);
        try
        {
            var result = EmbeddedToolLocator.TryResolve("uae-test-override-tool.dll", variable, () => "fallback-path");

            Assert.Equal(file, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            File.Delete(file);
        }
    }

    [Fact]
    public void Resolve_Throws_WhenTheToolIsNowhere()
    {
        Assert.Throws<FileNotFoundException>(() =>
            EmbeddedToolLocator.Resolve("uae-test-missing-tool.exe", "UAE_TEST_MISSING_TOOL_PATH", () => null));
    }
}
