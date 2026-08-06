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
}
