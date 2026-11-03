using System.Collections.Concurrent;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetEditor.Core.Editing;

namespace UAssetEditor.Core.Tests;

public class UnversionedConverterTests
{
    private static Usmap Schema()
    {
        var classProps = new ConcurrentDictionary<int, UsmapProperty>
        {
            [0] = new("Keep", 0, 0, 1, new UsmapPropertyData(UsmapPropertyType.IntProperty)),
            [1] = new("Node", 1, 0, 1, new UsmapStructData("NodeType")),
            [2] = new("Nodes", 2, 0, 1, new UsmapArrayData(UsmapPropertyType.ArrayProperty) { InnerType = new UsmapStructData("NodeType") }),
            [3] = new("Mode", 3, 0, 1, new UsmapEnumData("EMode", ["Off", "Auto"]) { InnerType = new UsmapPropertyData(UsmapPropertyType.ByteProperty) }),
            [4] = new("Size", 4, 0, 1, new UsmapStructData("PerPlatformFloat")),
        };
        var nodeProps = new ConcurrentDictionary<int, UsmapProperty>
        {
            [0] = new("Value", 0, 0, 1, new UsmapPropertyData(UsmapPropertyType.IntProperty)),
        };
        return new Usmap
        {
            Schemas = new Dictionary<string, UsmapSchema>(StringComparer.OrdinalIgnoreCase)
            {
                ["TestClass"] = new UsmapSchema("TestClass", null, classProps.Count, classProps, true, null),
                ["NodeType"] = new UsmapSchema("NodeType", null, nodeProps.Count, nodeProps, true, null),
            },
            EnumMap = new Dictionary<string, UsmapEnum>(StringComparer.OrdinalIgnoreCase)
            {
                ["EMode"] = new UsmapEnum("EMode", new ConcurrentDictionary<long, string>(new Dictionary<long, string> { [0] = "Off", [1] = "Auto" })),
            },
        };
    }

    private static StructPropertyData Node(UAsset asset, string name) => new(new FName(asset, name))
    {
        StructType = new FName(asset, "NodeType"),
        Value = [new IntPropertyData(new FName(asset, "Value")) { Value = 1 }, new IntPropertyData(new FName(asset, "Stray")) { Value = 2 }],
    };

    private static (UAsset Asset, NormalExport Export) VersionedAsset()
    {
        var asset = TestAssets.CreateAsset();
        asset.Imports.Add(new Import("/Script/CoreUObject", "Class", FPackageIndex.FromRawIndex(0), "TestClass", false, asset));
        var export = new NormalExport(asset, [])
        {
            ObjectName = new FName(asset, "Thing"),
            ClassIndex = FPackageIndex.FromImport(0),
            Data =
            [
                new IntPropertyData(new FName(asset, "Keep")) { Value = 7 },
                new IntPropertyData(new FName(asset, "EngineOnly")) { Value = 3 },
                Node(asset, "Node"),
                new ArrayPropertyData(new FName(asset, "Nodes")) { ArrayType = new FName(asset, "StructProperty"), Value = [Node(asset, "Nodes")] },
            ],
        };
        asset.Exports.Add(export);
        return (asset, export);
    }

    [Fact]
    public void Convert_MarksThePackageUnversioned_UnderTheGivenSchema()
    {
        var (asset, _) = VersionedAsset();
        var schema = Schema();

        UnversionedConverter.Convert(asset, schema);

        Assert.True(asset.HasUnversionedProperties);
        Assert.True(asset.IsUnversioned);
        Assert.Same(schema, asset.Mappings);
    }

    [Fact]
    public void Convert_DropsTopLevelPropertiesTheSchemaLacks_AndReportsThem()
    {
        var (asset, export) = VersionedAsset();

        var dropped = UnversionedConverter.Convert(asset, Schema());

        Assert.DoesNotContain(export.Data, p => p.Name.Value.Value == "EngineOnly");
        Assert.Contains(export.Data, p => p.Name.Value.Value == "Keep");
        Assert.Contains("Thing.EngineOnly", dropped);
    }

    [Fact]
    public void Convert_DropsUnknownFieldsInsideStructs_AndInsideArraysOfStructs()
    {
        var (asset, export) = VersionedAsset();

        var dropped = UnversionedConverter.Convert(asset, Schema());

        var node = (StructPropertyData)export.Data.Single(p => p.Name.Value.Value == "Node");
        Assert.Equal(["Value"], node.Value.Select(p => p.Name.Value.Value));
        var element = (StructPropertyData)((ArrayPropertyData)export.Data.Single(p => p.Name.Value.Value == "Nodes")).Value[0];
        Assert.Equal(["Value"], element.Value.Select(p => p.Name.Value.Value));
        Assert.Contains("Thing.Node.Stray", dropped);
        Assert.Contains("Thing.Nodes[0].Stray", dropped);
    }

    [Theory]
    [InlineData("EMode::Auto")]
    [InlineData("Auto")]
    public void Convert_MatchesEnumValuesToTheSchemasSpelling(string cooked)
    {
        var (asset, export) = VersionedAsset();
        export.Data.Add(new EnumPropertyData(new FName(asset, "Mode")) { EnumType = new FName(asset, "EMode"), Value = new FName(asset, cooked) });

        UnversionedConverter.Convert(asset, Schema());

        Assert.Equal("Auto", ((EnumPropertyData)export.Data.Single(p => p.Name.Value.Value == "Mode")).Value.Value.Value);
    }

    [Fact]
    public void Convert_RetypesANameValuedByteProperty_AsTheEnumTheSchemaDeclares()
    {
        var (asset, export) = VersionedAsset();
        export.Data.Add(new BytePropertyData(new FName(asset, "Mode"))
        {
            ByteType = BytePropertyType.FName,
            EnumType = new FName(asset, "EMode"),
            EnumValue = new FName(asset, "EMode::Auto"),
        });

        UnversionedConverter.Convert(asset, Schema());

        var mode = Assert.IsType<EnumPropertyData>(export.Data.Single(p => p.Name.Value.Value == "Mode"));
        Assert.Equal("EMode", mode.EnumType.Value.Value);
        Assert.Equal("Auto", mode.Value.Value.Value);
    }

    [Fact]
    public void Convert_RefusesAnEnumValueTheSchemaDoesNotHave()
    {
        var (asset, export) = VersionedAsset();
        export.Data.Add(new EnumPropertyData(new FName(asset, "Mode")) { EnumType = new FName(asset, "EMode"), Value = new FName(asset, "EMode::Missing") });

        var error = Assert.Throws<ArgumentException>(() => UnversionedConverter.Convert(asset, Schema()));
        Assert.Contains("EMode::Missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_KeepsAStructsNativePayload_WhichCarriesTheStructsOwnName()
    {
        var (asset, export) = VersionedAsset();
        export.Data.Add(new StructPropertyData(new FName(asset, "Size"))
        {
            StructType = new FName(asset, "PerPlatformFloat"),
            Value = [new FloatPropertyData(new FName(asset, "Size")) { Value = 1f }],
        });

        var dropped = UnversionedConverter.Convert(asset, Schema());

        var size = (StructPropertyData)export.Data.Single(p => p.Name.Value.Value == "Size");
        Assert.Single(size.Value);
        Assert.DoesNotContain("Thing.Size.Size", dropped);
    }

    [Fact]
    public void Convert_RefusesAPackageThatIsAlreadyUnversioned()
    {
        var (asset, _) = VersionedAsset();
        asset.PackageFlags |= EPackageFlags.PKG_UnversionedProperties;

        Assert.Throws<ArgumentException>(() => UnversionedConverter.Convert(asset, Schema()));
    }

    private static Usmap IntSchema(params string[] fields) =>
        TypedSchema([.. fields.Select(name => (name, UsmapPropertyType.IntProperty))]);

    private static Usmap TypedSchema(params (string Name, UsmapPropertyType Type)[] fields)
    {
        var properties = new ConcurrentDictionary<int, UsmapProperty>();
        for (var i = 0; i < fields.Length; i++)
            properties[i] = new UsmapProperty(fields[i].Name, (ushort)i, 0, 1, new UsmapPropertyData(fields[i].Type));
        return new Usmap
        {
            Schemas = new Dictionary<string, UsmapSchema>(StringComparer.OrdinalIgnoreCase)
            {
                ["TestClass"] = new UsmapSchema("TestClass", null, properties.Count, properties, true, null),
            },
            EnumMap = new Dictionary<string, UsmapEnum>(StringComparer.OrdinalIgnoreCase),
        };
    }

    // An unversioned package as a game build wrote it: Keep = 7, Later = 9 under the given schema.
    private static (UAsset Asset, NormalExport Export) UnversionedAsset(Usmap schema)
    {
        var (asset, export) = VersionedAsset();
        export.Data = [new IntPropertyData(new FName(asset, "Keep")) { Value = 7 }, new IntPropertyData(new FName(asset, "Later")) { Value = 9 }];
        asset.Mappings = schema;
        asset.PackageFlags |= EPackageFlags.PKG_UnversionedProperties;
        asset.IsUnversioned = true;
        export.ResolveAncestries(asset, new AncestryInfo());
        return (asset, export);
    }

    // Serialises the export as the asset is now, then reads the bytes back the way a game with that schema would.
    private static List<PropertyData> WriteThenReadWith(UAsset asset, Usmap schema)
    {
        using var stream = new MemoryStream();
        using (var writer = new AssetBinaryWriter(stream, Encoding.UTF8, leaveOpen: true, asset)) asset.Exports[0].Write(writer);
        asset.Mappings = schema;
        stream.Position = 0;
        var readBack = new NormalExport(asset, []) { ObjectName = asset.Exports[0].ObjectName, ClassIndex = asset.Exports[0].ClassIndex };
        using var reader = new AssetBinaryReader(stream, asset);
        readBack.Read(reader);
        return readBack.Data;
    }

    private static Dictionary<string, int> Values(List<PropertyData> data) =>
        data.Cast<IntPropertyData>().ToDictionary(p => p.Name.Value.Value, p => p.Value);

    [Fact]
    public void Unversioned_DataWrittenForAnOlderSchema_IsMisreadByOneThatInsertedAField()
    {
        var (asset, _) = UnversionedAsset(IntSchema("Keep", "Later"));

        var read = Values(WriteThenReadWith(asset, IntSchema("Keep", "Inserted", "Later")));

        Assert.Equal(new Dictionary<string, int> { ["Keep"] = 7, ["Inserted"] = 9 }, read);
    }

    [Fact]
    public void Reschema_LaysValuesOutForTheNewSchema_SoItReadsThemUnderTheirOwnNames()
    {
        var (asset, _) = UnversionedAsset(IntSchema("Keep", "Later"));
        var target = IntSchema("Keep", "Inserted", "Later");

        UnversionedConverter.Reschema(asset, target);

        Assert.Equal(new Dictionary<string, int> { ["Keep"] = 7, ["Later"] = 9 }, Values(WriteThenReadWith(asset, target)));
        Assert.True(asset.HasUnversionedProperties);
    }

    [Fact]
    public void Reschema_DropsAFieldTheNewSchemaRemoved_AndReportsIt()
    {
        var (asset, export) = UnversionedAsset(IntSchema("Keep", "Later"));

        var dropped = UnversionedConverter.Reschema(asset, IntSchema("Later"));

        Assert.Equal(["Thing.Keep"], dropped);
        Assert.Equal(["Later"], export.Data.Select(p => p.Name.Value.Value));
    }

    [Fact]
    public void Reschema_RefusesAFieldWhoseTypeChanged()
    {
        var (asset, _) = UnversionedAsset(IntSchema("Keep", "Later"));
        var retyped = TypedSchema(("Keep", UsmapPropertyType.IntProperty), ("Later", UsmapPropertyType.FloatProperty));

        var error = Assert.Throws<ArgumentException>(() => UnversionedConverter.Reschema(asset, retyped));
        Assert.Contains("Thing.Later is IntProperty", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reschema_RefusesATaggedPackage()
    {
        var (asset, _) = VersionedAsset();

        Assert.Throws<ArgumentException>(() => UnversionedConverter.Reschema(asset, Schema()));
    }
}
