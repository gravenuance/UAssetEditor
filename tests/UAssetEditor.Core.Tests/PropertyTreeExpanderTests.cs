using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests;

public class PropertyTreeExpanderTests
{
    [Fact]
    public void GetExportRoot_OnlyIncludesStructAndArrayProperties_NotScalars()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);

        var root = PropertyTreeExpander.GetExportRoot(export, asset);
        var names = root.Select(i => i.DisplayName).ToList();

        Assert.DoesNotContain(names, n => n.StartsWith("bEnabled"));
        Assert.DoesNotContain(names, n => n.StartsWith("Count"));
        Assert.DoesNotContain(names, n => n.StartsWith("DisplayName"));
        Assert.Contains(root, i => i.Path == "Location" && i.DisplayName == "Location (2)");
        Assert.Contains(root, i => i.Path == "Tags" && i.DisplayName == "Tags (2)");
    }

    [Fact]
    public void GetChildren_OnStructWithOnlyScalarFields_YieldsNothing()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        var location = PropertyTreeExpander.GetExportRoot(export, asset).Single(i => i.Path == "Location");

        // Location's own fields (X, Y) are plain floats - leaves, so they never become
        // their own tree entries; reaching them means opening Location itself into the
        // edit grid instead (see SearchService.PropertiesUnder).
        var fields = PropertyTreeExpander.GetChildren(location.Property, location.Path, asset);

        Assert.Empty(fields);
    }

    [Fact]
    public void GetChildren_OnArrayOfScalarElements_YieldsNothing()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        var tags = PropertyTreeExpander.GetExportRoot(export, asset).Single(i => i.Path == "Tags");

        var elements = PropertyTreeExpander.GetChildren(tags.Property, tags.Path, asset);

        Assert.Empty(elements);
    }

    [Fact]
    public void GetChildren_DescendsIntoAStructNestedInsideAnotherStruct()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        TestAssets.AddNestedStruct(asset, export);

        var root = PropertyTreeExpander.GetExportRoot(export, asset);
        var outerItem = Assert.Single(root, i => i.Path == "Outer");
        Assert.Equal("Outer (1)", outerItem.DisplayName);

        var innerChildren = PropertyTreeExpander.GetChildren(outerItem.Property, outerItem.Path, asset);
        var innerItem = Assert.Single(innerChildren);
        Assert.Equal("Outer.Inner", innerItem.Path);
        Assert.Equal("Inner (1)", innerItem.DisplayName);
    }

    [Fact]
    public void GetChildren_MapEntryWithOnlyScalarValues_IsExcludedEntirely()
    {
        var asset = TestAssets.CreateAsset();
        var map = TestAssets.CreateSampleMap(asset);

        // Scores' values are plain ints - leaves - so the map itself has nothing to show
        // as tree children (its entries are reached by opening it into the edit grid).
        var entries = PropertyTreeExpander.GetChildren(map, "Scores", asset);

        Assert.Empty(entries);
    }

    [Fact]
    public void GetExportRoot_ForDataTableExport_UsesTableRowsNotTheExportsOwnData()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleDataTableExport(asset);

        var root = PropertyTreeExpander.GetExportRoot(export, asset);

        // The row's own field (Damage) is a scalar leaf, so the row has nothing further
        // to show as a tree child, but the row itself is still a tree entry.
        var row = Assert.Single(root);
        Assert.Equal("Row1", row.Path);
        Assert.Empty(PropertyTreeExpander.GetChildren(row.Property, row.Path, asset));
    }
}
