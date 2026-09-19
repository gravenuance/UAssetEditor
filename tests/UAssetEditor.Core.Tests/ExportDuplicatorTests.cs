using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests;

public class ExportDuplicatorTests
{
    [Fact]
    public void Duplicate_AppendsANewExportAtTheEndOfTheExportList()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset, "SkeletalBodySetup");

        var newIndex = ExportDuplicator.Duplicate(asset, 0);

        Assert.Equal(1, newIndex);
        Assert.Equal(2, asset.Exports.Count);
    }

    [Fact]
    public void Duplicate_ProducesAnIndependentCopy_NotASharedDataList()
    {
        var asset = TestAssets.CreateAsset();
        var source = TestAssets.CreateSampleExport(asset, "SkeletalBodySetup");

        var newIndex = ExportDuplicator.Duplicate(asset, 0);
        var clone = (UAssetAPI.ExportTypes.NormalExport)asset.Exports[newIndex];

        // Editing the clone's Data list must not also mutate the source's.
        var clonedCount = (IntPropertyData)clone.Data.First(p => p.Name.Value?.Value == "Count");
        clonedCount.Value = 999;

        var sourceCount = (IntPropertyData)source.Data.First(p => p.Name.Value?.Value == "Count");
        Assert.Equal(5, sourceCount.Value);
        Assert.Equal(999, clonedCount.Value);
    }

    [Fact]
    public void Duplicate_GivesTheCloneANameDistinctFromEveryExportSharingItsOuter()
    {
        var asset = TestAssets.CreateAsset();
        // A freshly built export's OuterIndex defaults to 0 (top-level) - both source and clone
        // share that same default outer, which is exactly the collision case this guards against.
        TestAssets.CreateSampleExport(asset, "SkeletalBodySetup");

        var newIndex = ExportDuplicator.Duplicate(asset, 0);
        var clone = asset.Exports[newIndex];

        Assert.Equal("SkeletalBodySetup", clone.ObjectName.Value!.Value);
        Assert.Equal(1, clone.ObjectName.Number);

        // Duplicating again must not collide with the first clone's name either.
        var secondNewIndex = ExportDuplicator.Duplicate(asset, 0);
        Assert.Equal(2, asset.Exports[secondNewIndex].ObjectName.Number);
    }

    [Fact]
    public void Duplicate_CopiesOuterAndClassReferencesFromTheSource()
    {
        var asset = TestAssets.CreateAsset();
        var source = TestAssets.CreateSampleExport(asset, "SkeletalBodySetup");
        TestAssets.CreateSampleExport(asset, "PhysicsAsset"); // export 1, used as an arbitrary Outer target
        source.OuterIndex = FPackageIndex.FromExport(1);
        source.ClassIndex = FPackageIndex.FromImport(3);

        var newIndex = ExportDuplicator.Duplicate(asset, 0);
        var clone = asset.Exports[newIndex];

        Assert.Equal(source.OuterIndex.Index, clone.OuterIndex.Index);
        Assert.Equal(source.ClassIndex.Index, clone.ClassIndex.Index);
    }

    [Fact]
    public void Duplicate_ThrowsForAnOutOfRangeIndex()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);

        Assert.Throws<ArgumentException>(() => ExportDuplicator.Duplicate(asset, 5));
    }

    [Fact]
    public void Duplicate_ThrowsForANonNormalExport()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleDataTableExport(asset);

        Assert.Throws<ArgumentException>(() => ExportDuplicator.Duplicate(asset, 0));
    }
}
