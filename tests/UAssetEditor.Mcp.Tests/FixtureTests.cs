using UAssetEditor.Mcp.Tests.Rig;

namespace UAssetEditor.Mcp.Tests;

public sealed class FixtureTests
{
    [Fact]
    public void ASampleAsset_WrittenToDisk_ReadsBack()
    {
        using var folder = new TempFolder();

        var path = AssetFixtures.WriteSample(folder);

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(Path.ChangeExtension(path, ".uexp")));
        Assert.Equal("5", AssetFixtures.ReadProperty(path, "Count"));
    }
}
