using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.Core.Tests;

public class LooseFolderAssetSourceTests
{
    [Fact]
    public void EnumerateAssetPaths_ReturnsRootRelativeForwardSlashPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "UAssetEditor_LooseFolderTest_" + Guid.NewGuid());
        var nested = Path.Combine(root, "Game", "Content");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "Foo.uasset"), []);

        try
        {
            var source = new LooseFolderAssetSource(root);

            var paths = source.EnumerateAssetPaths().ToList();

            Assert.Equal(["Game/Content/Foo.uasset"], paths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
