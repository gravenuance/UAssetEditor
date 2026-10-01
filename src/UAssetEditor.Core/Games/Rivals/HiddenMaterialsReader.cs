using System.Globalization;
using System.Numerics;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.Games.Rivals;

/// <summary>
/// Fills <c>LODInfo[].DefaultHiddenMaterials</c> on a SkeletalMesh, from per-LOD bitmaps or from the hidden-materials
/// carrier export (an AssetUserData) cooked in the same package. Ported from repak-rivals' HiddenMaterialsReader.
/// </summary>
internal static class HiddenMaterialsReader
{
    private const string LodInfoName = "LODInfo";
    private const string DefaultHiddenMaterialsName = "DefaultHiddenMaterials";
    private const string LodMaterialMapName = "LODMaterialMap";

    /// <summary>Finds the first carrier export with a non-empty <c>LODHiddenMaterials</c> array and reads its flags.</summary>
    public static HiddenMaterialsResult ReadFromAsset(UAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var result = new HiddenMaterialsResult();

        for (var i = 0; i < asset.Exports.Count; i++)
        {
            var className = TryGetExportClassName(asset.Exports[i]);
            if (className == null || !LooksLikeCarrierClass(className)) continue;
            if (asset.Exports[i] is not NormalExport { Data: { } data }) continue;

            var lodArray = data.OfType<ArrayPropertyData>().FirstOrDefault(
                p => string.Equals(p.Name?.Value?.Value, "LODHiddenMaterials", StringComparison.OrdinalIgnoreCase));
            if (lodArray?.Value == null || lodArray.Value.Length == 0) continue;

            result.FoundUserData = true;
            result.Diagnostics.Add(
                $"Found LODHiddenMaterials on export[{i}] (class={className}), {lodArray.Value.Length} LOD(s)");

            foreach (var lodEntry in lodArray.Value)
            {
                if (lodEntry is not StructPropertyData lodStruct)
                {
                    result.PerLodFlags.Add([]);
                    continue;
                }

                var flags = ExtractBoolArray(lodStruct, "HiddenMaterials");
                result.PerLodFlags.Add(flags);
                result.Diagnostics.Add($"  LOD[{result.PerLodFlags.Count - 1}]: {flags.Length} slot flag(s)");
            }

            return result;
        }

        result.Diagnostics.Add("No AssetUserData carrier with LODHiddenMaterials found");
        return result;
    }

    /// <summary>Writes the carrier's flags into the mesh's LODInfo; returns how many LOD entries were changed.</summary>
    public static int InjectIntoLodInfo(NormalExport meshExport, HiddenMaterialsResult result, UAsset asset)
    {
        if (meshExport?.Data == null || result == null || !result.FoundUserData) return 0;

        var lodInfo = FindLodInfo(meshExport);
        if (lodInfo?.Value == null || lodInfo.Value.Length == 0)
        {
            result.Diagnostics.Add("LODInfo property not found on mesh export; nothing injected");
            return 0;
        }

        var modified = 0;
        for (var lodIndex = 0; lodIndex < lodInfo.Value.Length; lodIndex++)
        {
            if (lodInfo.Value[lodIndex] is not StructPropertyData lodStruct) continue;

            var flags = lodIndex < result.PerLodFlags.Count ? result.PerLodFlags[lodIndex] : [];
            if (flags.Length == 0 && Find(lodStruct, DefaultHiddenMaterialsName) == null) continue;

            InsertOrReplaceDefaultHiddenMaterials(lodStruct, BuildBoolArray(asset, flags));
            modified++;
        }

        lodInfo.DummyStruct = lodInfo.Value[0] as StructPropertyData ?? lodInfo.DummyStruct;
        result.Diagnostics.Add($"Injected DefaultHiddenMaterials into {modified} LOD entry/entries");
        return modified;
    }

    /// <summary>
    /// Writes one bitmap per LOD into the mesh's LODInfo; returns how many LOD entries were changed. One bitmap applies
    /// to every LOD. More bitmaps than the mesh has LODs is refused, because the extra masks would be dropped silently.
    /// </summary>
    /// <exception cref="InvalidDataException">There are more bitmaps than LODs.</exception>
    public static int InjectBitmapsIntoLodInfo(NormalExport meshExport, IReadOnlyList<ulong> bitmaps, UAsset asset)
    {
        if (meshExport?.Data == null || bitmaps == null || bitmaps.Count == 0) return 0;

        var lodInfo = FindLodInfo(meshExport);
        if (lodInfo?.Value == null || lodInfo.Value.Length == 0) return 0;

        if (bitmaps.Count > lodInfo.Value.Length)
        {
            throw new InvalidDataException(
                $"{bitmaps.Count} hidden-material bitmaps were given but the mesh '{meshExport.ObjectName?.Value?.Value}' has only "
                + $"{lodInfo.Value.Length} LOD(s); give at most one bitmap per LOD (a single bitmap applies to all of them).");
        }

        var modified = 0;
        var materialCount = SkeletalMeshMaterialCounter.Count(meshExport, asset);
        for (var i = 0; i < lodInfo.Value.Length; i++)
        {
            if (lodInfo.Value[i] is not StructPropertyData lodStruct) continue;

            var bitmap = bitmaps.Count == 1 ? bitmaps[0] : i < bitmaps.Count ? bitmaps[i] : bitmaps[^1];
            var boolCount = HiddenArrayLength(lodStruct, bitmap, materialCount);
            InsertOrReplaceDefaultHiddenMaterials(lodStruct, BuildBoolArray(asset, bitmap, boolCount));
            modified++;
        }

        lodInfo.DummyStruct = lodInfo.Value[0] as StructPropertyData ?? lodInfo.DummyStruct;
        return modified;
    }

    private static bool LooksLikeCarrierClass(string className) =>
        className.Contains("MaterialTagAssetUserData", StringComparison.OrdinalIgnoreCase)
        || className.Contains("HiddenMaterialsAssetUserData", StringComparison.OrdinalIgnoreCase)
        || className.Contains("RivalsMeshData", StringComparison.OrdinalIgnoreCase)
        || className.Contains("RivalsLODHiddenMaterialsData", StringComparison.OrdinalIgnoreCase);

    private static ArrayPropertyData? FindLodInfo(NormalExport export) =>
        export.Data.OfType<ArrayPropertyData>().FirstOrDefault(
            p => string.Equals(p.Name?.Value?.Value, LodInfoName, StringComparison.OrdinalIgnoreCase));

    private static PropertyData? Find(StructPropertyData property, string name) =>
        property.Value?.FirstOrDefault(
            child => string.Equals(child?.Name?.Value?.Value, name, StringComparison.OrdinalIgnoreCase));

    private static void InsertOrReplaceDefaultHiddenMaterials(StructPropertyData lodStruct, ArrayPropertyData replacement)
    {
        lodStruct.Value ??= [];

        var existingIndex = -1;
        var afterLodMaterialMap = -1;
        for (var i = 0; i < lodStruct.Value.Count; i++)
        {
            var name = lodStruct.Value[i]?.Name?.Value?.Value;
            if (name == null) continue;

            if (string.Equals(name, DefaultHiddenMaterialsName, StringComparison.OrdinalIgnoreCase))
            {
                existingIndex = i;
            }
            else if (string.Equals(name, LodMaterialMapName, StringComparison.OrdinalIgnoreCase))
            {
                afterLodMaterialMap = i + 1;
            }
        }

        if (existingIndex >= 0)
        {
            lodStruct.Value[existingIndex] = replacement;
            return;
        }

        var insertAt = afterLodMaterialMap >= 0 ? afterLodMaterialMap : Math.Min(3, lodStruct.Value.Count);
        lodStruct.Value.Insert(insertAt, replacement);
    }

    private static ArrayPropertyData BuildBoolArray(UAsset asset, bool[] flags)
    {
        var array = new ArrayPropertyData(new FName(asset, DefaultHiddenMaterialsName))
        {
            ArrayType = new FName(asset, "BoolProperty"),
            Value = new PropertyData[flags.Length],
        };

        for (var i = 0; i < flags.Length; i++)
        {
            array.Value[i] = new BoolPropertyData(FName.DefineDummy(asset, i.ToString(CultureInfo.InvariantCulture), int.MinValue))
            {
                Value = flags[i],
            };
        }

        return array;
    }

    private static ArrayPropertyData BuildBoolArray(UAsset asset, ulong bitmap, int count)
    {
        var flags = new bool[count];
        for (var i = 0; i < count; i++)
        {
            flags[i] = i < 64 && (bitmap & (1UL << i)) != 0;
        }

        return BuildBoolArray(asset, flags);
    }

    private static bool[] ExtractBoolArray(StructPropertyData container, string fieldName)
    {
        if (container.Value == null) return [];

        foreach (var field in container.Value)
        {
            if (!string.Equals(field?.Name?.Value?.Value, fieldName, StringComparison.OrdinalIgnoreCase)
                || field is not ArrayPropertyData { Value: { } items })
            {
                continue;
            }

            var output = new bool[items.Length];
            for (var i = 0; i < items.Length; i++)
            {
                output[i] = items[i] switch
                {
                    BoolPropertyData boolProperty => boolProperty.Value,
                    BytePropertyData byteProperty => byteProperty.Value != 0,
                    _ => false,
                };
            }

            return output;
        }

        return [];
    }

    private static int HiddenArrayLength(StructPropertyData lodStruct, ulong bitmap, int materialCount)
    {
        if (Find(lodStruct, DefaultHiddenMaterialsName) is ArrayPropertyData { Value.Length: > 0 } existing)
        {
            return existing.Value.Length;
        }

        if (materialCount > 0) return materialCount;
        return Math.Max(HighestSetBit(bitmap) + 1, 1);
    }

    private static int HighestSetBit(ulong value) => value == 0 ? -1 : 63 - BitOperations.LeadingZeroCount(value);

    private static string? TryGetExportClassName(Export export)
    {
        try
        {
            return export.GetExportClassType()?.Value?.Value;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
