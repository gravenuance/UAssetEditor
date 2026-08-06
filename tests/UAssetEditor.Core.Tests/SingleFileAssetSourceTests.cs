using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.Core.Tests;

public class SingleFileAssetSourceTests
{
    [Fact]
    public void EnumerateAssetPaths_ReturnsJustTheFileNameWithNoParentFolders()
    {
        var path = Path.Combine(Path.GetTempPath(), "UAssetEditor_SingleFileTest_" + Guid.NewGuid(), "Foo.uasset");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);

        try
        {
            var source = new SingleFileAssetSource(path);

            var paths = source.EnumerateAssetPaths().ToList();

            Assert.Equal(["Foo.uasset"], paths);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
