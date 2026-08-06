using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests;

public class PropertyLocatorTests
{
    [Fact]
    public void Locate_FindsPropertyByExportIndexAndPath()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);

        var node = PropertyLocator.Locate(asset, 0, "Location.X");

        Assert.NotNull(node);
        Assert.Equal("Location.X", node!.Path);
    }

    [Fact]
    public void Locate_ReturnsNullForUnknownPath()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);

        Assert.Null(PropertyLocator.Locate(asset, 0, "NoSuchProperty"));
    }

    [Fact]
    public void Locate_ReturnsNullForOutOfRangeExportIndex()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);

        Assert.Null(PropertyLocator.Locate(asset, 5, "Count"));
    }
}
