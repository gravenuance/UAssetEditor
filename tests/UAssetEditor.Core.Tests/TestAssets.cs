using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.Tests;

/// <summary>
/// Builds small in-memory <see cref="UAsset"/>/<see cref="NormalExport"/> fixtures for unit
/// tests, without needing a real cooked .uasset file on disk.
/// </summary>
internal static class TestAssets
{
    public static UAsset CreateAsset()
    {
        var asset = new UAsset(EngineVersion.VER_UE4_27);
        // A freshly constructed (fileless) UAsset has no initialized name map or
        // Exports/Imports lists - those are normally populated by Read(). Tests build
        // assets by hand, so set up the minimum state UAssetAPI expects to exist.
        asset.ClearNameIndexList();
        asset.Exports = new List<UAssetAPI.ExportTypes.Export>();
        asset.Imports = new List<Import>();
        return asset;
    }

    public static NormalExport CreateSampleExport(UAsset asset, string exportName = "TestExport")
    {
        var location = new StructPropertyData(new FName(asset, "Location"))
        {
            StructType = new FName(asset, "Vector"),
            Value = new List<PropertyData>
            {
                new FloatPropertyData(new FName(asset, "X")) { Value = 1.5f },
                new FloatPropertyData(new FName(asset, "Y")) { Value = 2.5f },
            },
        };

        var tags = new ArrayPropertyData(new FName(asset, "Tags"))
        {
            ArrayType = new FName(asset, "NameProperty"),
            Value = new PropertyData[]
            {
                new NamePropertyData(new FName(asset, "Tags")) { Value = new FName(asset, "Alpha") },
                new NamePropertyData(new FName(asset, "Tags")) { Value = new FName(asset, "Beta") },
            },
        };

        var export = new NormalExport(asset, Array.Empty<byte>())
        {
            ObjectName = new FName(asset, exportName),
            Data = new List<PropertyData>
            {
                new BoolPropertyData(new FName(asset, "bEnabled")) { Value = true },
                new IntPropertyData(new FName(asset, "Count")) { Value = 5 },
                new StrPropertyData(new FName(asset, "DisplayName")) { Value = new FString("Hello World") },
                location,
                tags,
            },
        };

        asset.Exports.Add(export);
        return export;
    }

    /// <summary>Adds an "Outer" struct (itself containing a nested "Inner" struct with one scalar field) to an already-built export's Data - exercises a table nested inside another table, rather than just a table of scalars.</summary>
    public static StructPropertyData AddNestedStruct(UAsset asset, NormalExport export)
    {
        var inner = new StructPropertyData(new FName(asset, "Inner"))
        {
            StructType = new FName(asset, "InnerType"),
            Value = new List<PropertyData>
            {
                new IntPropertyData(new FName(asset, "Value")) { Value = 7 },
            },
        };
        var outer = new StructPropertyData(new FName(asset, "Outer"))
        {
            StructType = new FName(asset, "OuterType"),
            Value = new List<PropertyData> { inner },
        };

        export.Data.Add(outer);
        return outer;
    }

    /// <summary>Adds a "MiddleContainer" struct whose only field is an entirely empty "EmptyInner" struct - a table that (however deep you go) never bottoms out in an actual editable leaf, for exercising <see cref="PropertyAccess.PropertyWalker.HasEditableDescendant"/>'s negative case.</summary>
    public static StructPropertyData AddPurelyStructuralStruct(UAsset asset, NormalExport export)
    {
        var emptyInner = new StructPropertyData(new FName(asset, "EmptyInner"))
        {
            StructType = new FName(asset, "EmptyInnerType"),
            Value = new List<PropertyData>(),
        };
        var middle = new StructPropertyData(new FName(asset, "MiddleContainer"))
        {
            StructType = new FName(asset, "MiddleContainerType"),
            Value = new List<PropertyData> { emptyInner },
        };

        export.Data.Add(middle);
        return middle;
    }

    /// <summary>A small NameProperty-to-IntProperty map, for exercising map-entry traversal.</summary>
    public static MapPropertyData CreateSampleMap(UAsset asset, string propertyName = "Scores")
    {
        var map = new MapPropertyData(new FName(asset, propertyName))
        {
            KeyType = new FName(asset, "NameProperty"),
            ValueType = new FName(asset, "IntProperty"),
            Value = new TMap<PropertyData, PropertyData>(),
        };
        map.Value.Add(
            new NamePropertyData(new FName(asset, "Key")) { Value = new FName(asset, "Alice") },
            new IntPropertyData(new FName(asset, "Value")) { Value = 10 });
        map.Value.Add(
            new NamePropertyData(new FName(asset, "Key")) { Value = new FName(asset, "Bob") },
            new IntPropertyData(new FName(asset, "Value")) { Value = 20 });
        return map;
    }

    /// <summary>An export whose only top-level property is a map - exercises map-entry traversal in <see cref="PropertyAccess.PropertyWalker"/>/<see cref="PropertyAccess.PropertyTreeExpander"/>.</summary>
    public static NormalExport CreateExportWithMap(UAsset asset, string exportName = "MapExport")
    {
        var export = new NormalExport(asset, Array.Empty<byte>())
        {
            ObjectName = new FName(asset, exportName),
            Data = new List<PropertyData> { CreateSampleMap(asset) },
        };

        asset.Exports.Add(export);
        return export;
    }

    /// <summary>A DataTableExport whose rows (each a struct) live in Table.Data rather than the export's own (empty) Data - exercises the DataTable-specific root in <see cref="PropertyAccess.PropertyTreeExpander"/>.</summary>
    public static DataTableExport CreateSampleDataTableExport(UAsset asset, string exportName = "TestDataTable")
    {
        var row = new StructPropertyData(new FName(asset, "Row1"))
        {
            StructType = new FName(asset, "TestRow"),
            Value = new List<PropertyData>
            {
                new IntPropertyData(new FName(asset, "Damage")) { Value = 42 },
            },
        };

        var table = new UDataTable(new List<StructPropertyData> { row });
        var export = new DataTableExport(table, asset, Array.Empty<byte>())
        {
            ObjectName = new FName(asset, exportName),
        };

        asset.Exports.Add(export);
        return export;
    }
}
