using UAssetAPI.PropertyTypes.Objects;
using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests;

public class PropertyWalkerTests
{
    [Fact]
    public void Walk_YieldsTopLevelAndNestedPaths()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);

        var paths = PropertyWalker.Walk(export).Select(n => n.Path).ToList();

        Assert.Contains("bEnabled", paths);
        Assert.Contains("Count", paths);
        Assert.Contains("DisplayName", paths);
        Assert.Contains("Location", paths);
        Assert.Contains("Location.X", paths);
        Assert.Contains("Location.Y", paths);
        Assert.Contains("Tags", paths);
        Assert.Contains("Tags[0]", paths);
        Assert.Contains("Tags[1]", paths);
    }

    [Fact]
    public void Walk_ExposesOwnerForInPlaceMutation()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);

        var countNode = PropertyWalker.Walk(export).Single(n => n.Path == "Count");

        Assert.NotNull(countNode.Owner);
        Assert.Same(export.Data, countNode.Owner);
        Assert.Same(export.Data[countNode.OwnerIndex], countNode.Property);
    }

    [Fact]
    public void Walk_DistinguishesSiblingsThatShareAnFNameByNumber()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateExportWithNumberedSiblings(asset);

        var paths = PropertyWalker.Walk(export).Select(n => n.Path).ToList();

        Assert.Contains("Node", paths);
        Assert.Contains("Node_0", paths);
        Assert.Contains("Node_1", paths);
        // Distinct paths just proves they don't collide by NAME - confirm each also still
        // resolves back to its own distinct value, not silently the wrong sibling's.
        Assert.Equal(0, ((IntPropertyData)PropertyWalker.Walk(export).Single(n => n.Path == "Node").Property).Value);
        Assert.Equal(1, ((IntPropertyData)PropertyWalker.Walk(export).Single(n => n.Path == "Node_0").Property).Value);
        Assert.Equal(2, ((IntPropertyData)PropertyWalker.Walk(export).Single(n => n.Path == "Node_1").Property).Value);
    }

    [Fact]
    public void Walk_DescendsIntoMapEntriesByKey()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateExportWithMap(asset);

        var paths = PropertyWalker.Walk(export).Select(n => n.Path).ToList();

        Assert.Contains("Scores[Alice]", paths);
        Assert.Contains("Scores[Bob]", paths);
    }

    [Fact]
    public void Walk_ForDataTableExport_YieldsRowFieldsFromTableDataNotTheExportsOwnData()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleDataTableExport(asset);

        var paths = PropertyWalker.Walk(export).Select(n => n.Path).ToList();

        Assert.Contains("Row1", paths);
        Assert.Contains("Row1.Damage", paths);
    }
}
