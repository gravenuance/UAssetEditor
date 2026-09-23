using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
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

    /// <summary>
    /// Adds an array of small structs (each a single "Value" int field) to an already-built
    /// export's Data - the shape a KawaiiPhysics node's "Chains" array actually has, for
    /// exercising array-element duplication/removal rather than the scalar-element "Tags"
    /// array <see cref="CreateSampleExport"/> already builds.
    /// </summary>
    public static ArrayPropertyData AddStructArray(UAsset asset, NormalExport export, string arrayName, params int[] values)
    {
        var elements = values.Select(v => (PropertyData)new StructPropertyData(new FName(asset, arrayName))
        {
            StructType = new FName(asset, arrayName + "Type"),
            Value = new List<PropertyData> { new IntPropertyData(new FName(asset, "Value")) { Value = v } },
        }).ToArray();

        var array = new ArrayPropertyData(new FName(asset, arrayName))
        {
            ArrayType = new FName(asset, "StructProperty"),
            Value = elements,
        };

        export.Data.Add(array);
        return array;
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

    /// <summary>
    /// An export whose Data list holds three "Node" siblings using Unreal's real FName.Number
    /// encoding (Number 0 = bare, Number K >= 1 displays as "_{K-1}") rather than the suffix
    /// baked into the string - the shape a legacy/versioned-format asset's top-level properties
    /// actually have (confirmed against a real retoc-converted file this session), as opposed to
    /// an unversioned CDO's own Data list, which bakes the suffix into the string instead (see
    /// <see cref="CreateClassWithNumberedNode"/>). Exercises <see cref="PropertyAccess.PropertyPaths.Child"/>'s
    /// FNameDisplay reconstruction - without it, all three siblings collapse to the identical bare path "Node".
    /// </summary>
    public static NormalExport CreateExportWithNumberedSiblings(UAsset asset, string exportName = "NumberedExport")
    {
        var export = new NormalExport(asset, Array.Empty<byte>())
        {
            ObjectName = new FName(asset, exportName),
            Data = new List<PropertyData>
            {
                new IntPropertyData(new FName(asset, "Node")) { Value = 0 },
                new IntPropertyData(new FName(asset, "Node", 1)) { Value = 1 },
                new IntPropertyData(new FName(asset, "Node", 2)) { Value = 2 },
            },
        };

        asset.Exports.Add(export);
        return export;
    }

    /// <summary>
    /// Same real Number-based FName encoding as <see cref="CreateExportWithNumberedSiblings"/>,
    /// but each sibling is a non-empty struct (rather than a scalar) - the shape
    /// <see cref="PropertyAccess.PropertyTreeExpander.GetExportRoot"/> needs, since it filters
    /// out childless (scalar) properties entirely and so can't exercise its own DisplayName
    /// construction against a plain <see cref="IntPropertyData"/> sibling set.
    /// </summary>
    public static NormalExport CreateExportWithNumberedStructSiblings(UAsset asset, string exportName = "NumberedStructExport")
    {
        StructPropertyData Node(FName name, int value) => new(name)
        {
            StructType = new FName(asset, "NodeType"),
            Value = new List<PropertyData> { new IntPropertyData(new FName(asset, "Value")) { Value = value } },
        };

        var export = new NormalExport(asset, Array.Empty<byte>())
        {
            ObjectName = new FName(asset, exportName),
            Data = new List<PropertyData>
            {
                Node(new FName(asset, "Node"), 0),
                Node(new FName(asset, "Node", 1), 1),
                Node(new FName(asset, "Node", 2), 2),
            },
        };

        asset.Exports.Add(export);
        return export;
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

    /// <summary>
    /// A minimal Blueprint-style class export (LoadedProperties declaring one struct-typed
    /// node property) paired with its CDO export (holding that property's default value) -
    /// the shape <see cref="PropertyAccess.ClassPropertyDeclarer"/> needs: a class whose
    /// ClassDefaultObject points back to the CDO export that carries the actual value.
    /// </summary>
    public static (ClassExport ClassExport, NormalExport Cdo) CreateClassWithNode(
        UAsset asset, string nodePropertyName, string className = "TestClass_C", string cdoName = "Default__TestClass_C")
    {
        var cdo = new NormalExport(asset, Array.Empty<byte>())
        {
            ObjectName = new FName(asset, cdoName),
            Data = new List<PropertyData>
            {
                new StructPropertyData(new FName(asset, nodePropertyName))
                {
                    StructType = new FName(asset, "TestNodeType"),
                    Value = new List<PropertyData> { new IntPropertyData(new FName(asset, "Value")) { Value = 1 } },
                },
            },
        };
        asset.Exports.Add(cdo);
        var cdoIndex = asset.Exports.Count - 1;
        asset.Imports.Add(new Import("/Script/CoreUObject", "ScriptStruct", FPackageIndex.FromRawIndex(0), "TestNodeType", false, asset));

        var classExport = new ClassExport
        {
            Asset = asset,
            ObjectName = new FName(asset, className),
            LoadedProperties = new FProperty[]
            {
                new FStructProperty
                {
                    Name = new FName(asset, nodePropertyName),
                    SerializedType = new FName(asset, "StructProperty"),
                    Struct = FPackageIndex.FromImport(asset.Imports.Count - 1),
                    ElementSize = 8,
                },
            },
            ClassDefaultObject = FPackageIndex.FromExport(cdoIndex),
            OuterIndex = new FPackageIndex(0),
            SuperStruct = new FPackageIndex(0),
        };
        asset.Exports.Add(classExport);

        return (classExport, cdo);
    }

    /// <summary>
    /// Same shape as <see cref="CreateClassWithNode"/>, except the LoadedProperties entry uses
    /// Unreal's own real encoding for a "Foo_N" sibling - FName(String="Foo", Number=N+1) - rather
    /// than one literal "Foo_N" string with Number 0. Confirmed against a real compiled Marvel
    /// Rivals anim-blueprint class via reflection: every "AnimGraphNode_KawaiiPhysics_N" declared
    /// on the class is FName("AnimGraphNode_KawaiiPhysics", Number=N+1); only the CDO's own Data
    /// list bakes the suffix into the string (Number 0). <see cref="displayNumber"/> is the "_N"
    /// suffix as it would display (e.g. 34 for "..._34"); pass 0 for the bare, unsuffixed name.
    /// </summary>
    public static (ClassExport ClassExport, NormalExport Cdo) CreateClassWithNumberedNode(
        UAsset asset, string baseName, int displayNumber, string className = "TestClass_C", string cdoName = "Default__TestClass_C")
    {
        var displayName = displayNumber == 0 ? baseName : $"{baseName}_{displayNumber}";

        var cdo = new NormalExport(asset, Array.Empty<byte>())
        {
            ObjectName = new FName(asset, cdoName),
            Data = new List<PropertyData>
            {
                new StructPropertyData(new FName(asset, displayName))
                {
                    StructType = new FName(asset, "TestNodeType"),
                    Value = new List<PropertyData> { new IntPropertyData(new FName(asset, "Value")) { Value = 1 } },
                },
            },
        };
        asset.Exports.Add(cdo);
        var cdoIndex = asset.Exports.Count - 1;

        var classExport = new ClassExport
        {
            Asset = asset,
            ObjectName = new FName(asset, className),
            LoadedProperties = new FProperty[]
            {
                new FStructProperty
                {
                    Name = new FName(asset, baseName, displayNumber + 1),
                    Struct = FPackageIndex.FromImport(0),
                    ElementSize = 8,
                },
            },
            ClassDefaultObject = FPackageIndex.FromExport(cdoIndex),
        };
        asset.Exports.Add(classExport);

        return (classExport, cdo);
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
    /// <summary>
    /// Gives an anim blueprint's CDO sparse class data holding one exposed-value handler per entry
    /// ("None" = a handler that does nothing), laid out as a cooked CDO carries it.
    /// </summary>
    public static void AddSparseHandlers(UAsset asset, NormalExport cdo, params string[] boundFunctions)
    {
        var structExport = new StructExport
        {
            Asset = asset,
            ObjectName = new FName(asset, "AnimBlueprintGeneratedConstantData"),
            LoadedProperties = [],
            OuterIndex = new FPackageIndex(0),
            SuperStruct = new FPackageIndex(0),
        };
        asset.Exports.Add(structExport);

        var handlers = boundFunctions.Select(f => (PropertyData)new StructPropertyData(new FName(asset, "ExposedValueHandlers"))
        {
            StructType = new FName(asset, "ExposedValueHandler"),
            Value = [new NamePropertyData(new FName(asset, "BoundFunction")) { Value = new FName(asset, f) }],
        }).ToArray();
        var sparse = new StructPropertyData(new FName(asset, "SparseClassData"))
        {
            StructType = new FName(asset, "AnimBlueprintGeneratedConstantData"),
            Value =
            [
                new StructPropertyData(new FName(asset, "AnimBlueprintExtension_Base"))
                {
                    StructType = new FName(asset, "AnimSubsystem_Base"),
                    Value = [new ArrayPropertyData(new FName(asset, "ExposedValueHandlers")) { ArrayType = new FName(asset, "StructProperty"), Value = handlers }],
                },
            ],
        };

        using var stream = new MemoryStream();
        using (var writer = new AssetBinaryWriter(stream, asset))
        {
            writer.Write(FPackageIndex.FromExport(asset.Exports.Count - 1).Index);
            sparse.Write(writer, false);
        }
        cdo.Extras = stream.ToArray();
    }

    /// <summary>The BoundFunction of each exposed-value handler in the CDO's sparse class data.</summary>
    public static string[] SparseHandlers(UAsset asset, NormalExport cdo) =>
        PropertyAccess.SparseClassData.Read(asset, cdo)!.Find<ArrayPropertyData>("AnimBlueprintExtension_Base.ExposedValueHandlers")!.Value
            .Cast<StructPropertyData>()
            .Select(h => h.Value.OfType<NamePropertyData>().Single().Value.ToString())
            .ToArray();
}
