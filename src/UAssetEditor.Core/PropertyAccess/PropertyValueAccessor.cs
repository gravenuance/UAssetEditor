using System.Globalization;
using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// Reads and writes the small set of property kinds this tool understands as searchable/
/// editable text: scalars, strings, names, text, and object/soft-object references.
/// Property types outside this set (the other ~90 UAssetAPI supports) are treated as
/// unsupported for search/edit and are simply skipped.
/// </summary>
public static class PropertyValueAccessor
{
    public static string? AsSearchableString(PropertyData prop, UAsset asset)
    {
        ArgumentNullException.ThrowIfNull(prop);
        ArgumentNullException.ThrowIfNull(asset);

        return prop switch
        {
            BoolPropertyData b => b.Value.ToString(),
            IntPropertyData i => i.Value.ToString(CultureInfo.InvariantCulture),
            Int64PropertyData i64 => i64.Value.ToString(CultureInfo.InvariantCulture),
            Int8PropertyData i8 => i8.Value.ToString(CultureInfo.InvariantCulture),
            Int16PropertyData i16 => i16.Value.ToString(CultureInfo.InvariantCulture),
            UInt16PropertyData u16 => u16.Value.ToString(CultureInfo.InvariantCulture),
            UInt32PropertyData u32 => u32.Value.ToString(CultureInfo.InvariantCulture),
            UInt64PropertyData u64 => u64.Value.ToString(CultureInfo.InvariantCulture),
            FloatPropertyData f => f.Value.ToString(CultureInfo.InvariantCulture),
            DoublePropertyData d => d.Value.ToString(CultureInfo.InvariantCulture),
            StrPropertyData s => s.Value?.Value,
            NamePropertyData n => n.Value?.Value?.Value,
            TextPropertyData t => t.Value?.Value ?? t.CultureInvariantString?.Value,
            ObjectPropertyData o => DescribeObjectReference(o.Value, asset),
            SoftObjectPropertyData so => DescribeSoftObjectPath(so.Value),
            SoftObjectPathPropertyData sop => sop.Path?.Value,
            EnumPropertyData e => e.Value?.Value?.Value,
            BytePropertyData bp => bp.ByteType == BytePropertyType.FName ? bp.EnumValue?.Value?.Value : bp.Value.ToString(CultureInfo.InvariantCulture),
            IntPointPropertyData ip when ip.Value is { Length: 2 } v => $"{v[0]},{v[1]}",
            _ => null,
        };
    }

    public static bool TrySetStringValue(PropertyData prop, string newValue, UAsset asset)
    {
        ArgumentNullException.ThrowIfNull(prop);
        ArgumentNullException.ThrowIfNull(newValue);
        ArgumentNullException.ThrowIfNull(asset);

        switch (prop)
        {
            case BoolPropertyData b when bool.TryParse(newValue, out var bv):
                b.Value = bv;
                return true;
            case IntPropertyData ip when int.TryParse(newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv):
                ip.Value = iv;
                return true;
            case Int64PropertyData i64 when long.TryParse(newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv):
                i64.Value = lv;
                return true;
            case Int8PropertyData i8 when sbyte.TryParse(newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i8v):
                i8.Value = i8v;
                return true;
            case Int16PropertyData i16 when short.TryParse(newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i16v):
                i16.Value = i16v;
                return true;
            case UInt16PropertyData u16 when ushort.TryParse(newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u16v):
                u16.Value = u16v;
                return true;
            case UInt32PropertyData u32 when uint.TryParse(newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u32v):
                u32.Value = u32v;
                return true;
            case UInt64PropertyData u64 when ulong.TryParse(newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u64v):
                u64.Value = u64v;
                return true;
            case FloatPropertyData fp when float.TryParse(newValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv):
                fp.Value = fv;
                return true;
            case DoublePropertyData dp when double.TryParse(newValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv):
                dp.Value = dv;
                return true;
            case StrPropertyData s:
                s.Value = new FString(newValue);
                return true;
            case NamePropertyData n:
                n.Value = new FName(asset, newValue);
                return true;
            case TextPropertyData t:
                t.Value = new FString(newValue);
                return true;
            case EnumPropertyData e:
                e.Value = new FName(asset, newValue);
                return true;
            case BytePropertyData bp when bp.ByteType == BytePropertyType.FName:
                bp.EnumValue = new FName(asset, newValue);
                return true;
            case BytePropertyData bp when byte.TryParse(newValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var byv):
                bp.Value = byv;
                return true;
            case IntPointPropertyData ip:
                var parts = newValue.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length != 2
                    || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                    || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
                    return false;
                ip.Value = [x, y];
                return true;
            case ObjectPropertyData op:
                var importIndex = ImportPathResolver.FindImportIndex(asset, newValue);
                if (importIndex is null) return false;
                op.Value = FPackageIndex.FromImport(importIndex.Value);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Recomputes <see cref="PropertyData.IsZero"/> from the property's current value.
    /// UAssetAPI's serializer uses this flag to decide whether a property can be
    /// omitted as a default; it must stay in sync whenever a rule mutates a value; a
    /// stale flag left over from before the edit both is not stripped and can result in
    /// state which is not properly serialized.
    /// </summary>
    public static void UpdateIsZeroFlag(PropertyData prop)
    {
        ArgumentNullException.ThrowIfNull(prop);

        prop.IsZero = prop switch
        {
            BoolPropertyData b => !b.Value,
            IntPropertyData i => i.Value == 0,
            Int64PropertyData i64 => i64.Value == 0,
            Int8PropertyData i8 => i8.Value == 0,
            Int16PropertyData i16 => i16.Value == 0,
            UInt16PropertyData u16 => u16.Value == 0,
            UInt32PropertyData u32 => u32.Value == 0,
            UInt64PropertyData u64 => u64.Value == 0,
            FloatPropertyData f => f.Value == 0f,
            DoublePropertyData d => d.Value == 0d,
            StrPropertyData s => string.IsNullOrEmpty(s.Value?.Value),
            NamePropertyData n => string.IsNullOrEmpty(n.Value?.Value?.Value),
            TextPropertyData t => string.IsNullOrEmpty(t.Value?.Value),
            BytePropertyData { ByteType: BytePropertyType.Byte } bp => bp.Value == 0,
            IntPointPropertyData ip => ip.Value is null or [0, 0],
            _ => prop.IsZero,
        };
    }

    private static string DescribeObjectReference(FPackageIndex index, UAsset asset)
    {
        if (index.IsNull()) return "";
        if (index.IsImport()) return ImportPathResolver.GetFullPath(index.ToImport(asset), asset);
        if (index.IsExport() && index.Index - 1 < asset.Exports.Count)
            return asset.Exports[index.Index - 1].ObjectName.Value?.Value ?? "";
        return "";
    }

    private static string DescribeSoftObjectPath(FSoftObjectPath path)
    {
        var package = path.AssetPath.PackageName?.Value?.Value ?? "";
        var assetName = path.AssetPath.AssetName?.Value?.Value ?? "";
        var basePath = $"{package}.{assetName}";
        var sub = path.SubPathString?.Value;
        return string.IsNullOrEmpty(sub) ? basePath : $"{basePath}:{sub}";
    }
}
