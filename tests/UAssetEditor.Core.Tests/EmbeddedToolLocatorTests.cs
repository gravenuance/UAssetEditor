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
}
