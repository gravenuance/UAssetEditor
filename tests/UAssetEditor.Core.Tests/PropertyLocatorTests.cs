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

    [Fact]
    public void Locate_FindsAndEditsAMapEntryByPath()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateExportWithMap(asset);

        var node = PropertyLocator.Locate(asset, 0, "Scores[Alice]");

        Assert.NotNull(node);
        Assert.Equal("10", PropertyValueAccessor.AsSearchableString(node!.Property, asset));

        Assert.True(PropertyValueAccessor.TrySetStringValue(node.Property, "99", asset));
        Assert.Equal("99", PropertyValueAccessor.AsSearchableString(PropertyLocator.Locate(asset, 0, "Scores[Alice]")!.Property, asset));
    }

    [Fact]
    public void Locate_FindsAndEditsADataTableRowFieldByPath()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleDataTableExport(asset);

        var node = PropertyLocator.Locate(asset, 0, "Row1.Damage");

        Assert.NotNull(node);
        Assert.Equal("42", PropertyValueAccessor.AsSearchableString(node!.Property, asset));

        Assert.True(PropertyValueAccessor.TrySetStringValue(node.Property, "100", asset));
        Assert.Equal("100", PropertyValueAccessor.AsSearchableString(PropertyLocator.Locate(asset, 0, "Row1.Damage")!.Property, asset));
    }
}
