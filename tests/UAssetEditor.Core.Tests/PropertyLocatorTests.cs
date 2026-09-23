using System.Collections.Concurrent;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
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

    [Fact]
    public void LocateArrayElement_FindsTheOwningArrayAndIndex()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        var chains = TestAssets.AddStructArray(asset, export, "Chains", 10, 20);

        var located = PropertyLocator.LocateArrayElement(asset, 0, "Chains[1]");

        Assert.NotNull(located);
        Assert.Same(chains, located!.Value.Array);
        Assert.Equal(1, located.Value.Index);
    }

    [Fact]
    public void LocateArrayElement_ReturnsNullForAPlainStructField()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);

        Assert.Null(PropertyLocator.LocateArrayElement(asset, 0, "Location.X"));
    }

    [Fact]
    public void LocateArrayElement_ReturnsNullForAMapEntry()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateExportWithMap(asset);

        // "Scores[Alice]" has a trailing bracket too, but "Alice" isn't an integer index and
        // the resolved parent (Scores) is a map, not an array - must not be mistaken for one.
        Assert.Null(PropertyLocator.LocateArrayElement(asset, 0, "Scores[Alice]"));
    }

    [Fact]
    public void LocateArrayElement_ReturnsNullForAnUnknownPath()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);

        Assert.Null(PropertyLocator.LocateArrayElement(asset, 0, "NoSuchArray[0]"));
    }

    [Fact]
    public void Locate_AgreesWithAWholeTreeWalkOnEveryPath()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        TestAssets.AddNestedStruct(asset, export);
        TestAssets.AddStructArray(asset, export, "Chains", 1, 2, 3);
        export.Data.Add(TestAssets.CreateSampleMap(asset));

        foreach (var expected in PropertyWalker.Walk(export))
        {
            var actual = PropertyLocator.Locate(asset, 0, expected.Path);
            Assert.NotNull(actual);
            Assert.Same(expected.Property, actual!.Property);
            Assert.Same(expected.Owner, actual.Owner);
            Assert.Equal(expected.OwnerIndex, actual.OwnerIndex);
        }
    }

    [Fact]
    public void LocateOrCreate_AddsAFieldSittingAtItsDefaultFromTheSchema()
    {
        var (asset, node) = CreateNodeWithSchema();

        var created = PropertyLocator.LocateOrCreate(asset, 0, "Node.Scale");

        var scale = Assert.IsType<FloatPropertyData>(created?.Property);
        Assert.True(scale.IsZero);
        Assert.Equal("TestNodeType", scale.Ancestry.Parent.ToString());
        Assert.Contains(scale, node.Value);
    }

    [Fact]
    public void LocateOrCreate_AddsAVectorStructReadyToSet()
    {
        var (asset, _) = CreateNodeWithSchema();

        var created = PropertyLocator.LocateOrCreate(asset, 0, "Node.Gravity.Gravity");

        Assert.NotNull(created);
        Assert.True(PropertyValueAccessor.TrySetStringValue(created!.Property, "0,0,-980", asset));
        Assert.Equal("0,0,-980", PropertyValueAccessor.AsSearchableString(PropertyLocator.Locate(asset, 0, "Node.Gravity.Gravity")!.Property, asset));
    }

    [Fact]
    public void LocateOrCreate_LeavesTheAssetAloneForANameTheSchemaDoesNotKnow()
    {
        var (asset, node) = CreateNodeWithSchema();

        Assert.Null(PropertyLocator.LocateOrCreate(asset, 0, "Node.Scael"));
        Assert.Single(node.Value);
    }

    [Fact]
    public void LocateOrCreate_UndoesPartialCreationWhenALaterStepFails()
    {
        var (asset, node) = CreateNodeWithSchema();

        Assert.Null(PropertyLocator.LocateOrCreate(asset, 0, "Node.Gravity.Nope"));
        Assert.Single(node.Value);
    }

    [Fact]
    public void LocateOrCreate_NeverInventsArrayElements()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        var before = export.Data.Count;

        Assert.Null(PropertyLocator.LocateOrCreate(asset, 0, "Tags[5]"));
        Assert.Equal(before, export.Data.Count);
    }

    private static (UAsset Asset, StructPropertyData Node) CreateNodeWithSchema()
    {
        var asset = TestAssets.CreateAsset();
        var props = new ConcurrentDictionary<int, UsmapProperty>
        {
            [0] = new("Value", 0, 0, 1, new UsmapPropertyData(UsmapPropertyType.IntProperty)),
            [1] = new("Scale", 1, 0, 1, new UsmapPropertyData(UsmapPropertyType.FloatProperty)),
            [2] = new("Gravity", 2, 0, 1, new UsmapStructData("Vector")),
        };
        asset.Mappings = new Usmap
        {
            Schemas = new Dictionary<string, UsmapSchema>(StringComparer.OrdinalIgnoreCase)
            {
                ["TestNodeType"] = new UsmapSchema("TestNodeType", null, props.Count, props, true, null),
            },
        };

        var node = new StructPropertyData(new FName(asset, "Node"))
        {
            StructType = new FName(asset, "TestNodeType"),
            Value = [new IntPropertyData(new FName(asset, "Value")) { Value = 1 }],
        };
        asset.Exports.Add(new NormalExport(asset, []) { ObjectName = new FName(asset, "Export"), Data = [node] });
        node.ResolveAncestries(asset, new AncestryInfo());
        return (asset, node);
    }
}
