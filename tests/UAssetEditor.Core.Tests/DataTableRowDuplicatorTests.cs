using UAssetAPI.PropertyTypes.Objects;
using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests;

public class DataTableRowDuplicatorTests
{
    [Fact]
    public void Duplicate_AppendsARenamedCopyAsTheLastRow()
    {
        var asset = TestAssets.CreateAsset();
        var table = TestAssets.CreateSampleDataTableExport(asset);

        DataTableRowDuplicator.Duplicate(asset, 0, "Row1", "Row1_Mini");

        Assert.Equal(2, table.Table.Data.Count);
        Assert.Equal("Row1_Mini", table.Table.Data[1].Name.Value!.Value);
        Assert.Equal(42, ((IntPropertyData)table.Table.Data[1].Value[0]).Value);
    }

    [Fact]
    public void Duplicate_ProducesAnIndependentCopy()
    {
        var asset = TestAssets.CreateAsset();
        var table = TestAssets.CreateSampleDataTableExport(asset);

        var clone = DataTableRowDuplicator.Duplicate(asset, 0, "Row1", "Row2");
        ((IntPropertyData)clone.Value[0]).Value = 7;

        Assert.Equal(42, ((IntPropertyData)table.Table.Data[0].Value[0]).Value);
    }

    [Fact]
    public void Duplicate_AddsTheNewRowNameToTheNameMap()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleDataTableExport(asset);

        DataTableRowDuplicator.Duplicate(asset, 0, "Row1", "Row2");

        Assert.Contains(asset.GetNameMapIndexList(), n => n.Value == "Row2");
    }

    [Fact]
    public void Duplicate_RefusesANameThatAlreadyExists()
    {
        var asset = TestAssets.CreateAsset();
        var table = TestAssets.CreateSampleDataTableExport(asset);

        Assert.Throws<ArgumentException>(() => DataTableRowDuplicator.Duplicate(asset, 0, "Row1", "Row1"));
        Assert.Single(table.Table.Data);
    }

    [Fact]
    public void Duplicate_RefusesAMissingSourceRow()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleDataTableExport(asset);

        Assert.Throws<ArgumentException>(() => DataTableRowDuplicator.Duplicate(asset, 0, "Nope", "Row2"));
    }

    [Fact]
    public void Duplicate_RefusesAnExportThatIsNotADataTable()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);

        Assert.Throws<ArgumentException>(() => DataTableRowDuplicator.Duplicate(asset, 0, "Row1", "Row2"));
    }
}
