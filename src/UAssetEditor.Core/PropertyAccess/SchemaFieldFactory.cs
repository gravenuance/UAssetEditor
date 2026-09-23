using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// Builds a field that unversioned data left out because it sat at its default, using the schema
/// the writer will serialize it with. The new field starts at its default value.
/// </summary>
internal static class SchemaFieldFactory
{
    /// <summary>Null when the owner's schema has no such field, or it is of a kind this can't build.</summary>
    public static PropertyData? Create(UAsset asset, IList<PropertyData> siblings, StructPropertyData? owner, string name)
    {
        if (asset.Mappings == null) return null;

        var ancestry = owner != null ? owner.ChildAncestry(asset, owner.Ancestry) : siblings.FirstOrDefault()?.Ancestry;
        if (ancestry == null) return null;

        var fieldName = new FName(asset, name);
        if (TryFindField(asset, fieldName, ancestry) is not { } schemaField) return null;

        var created = Build(asset, fieldName, schemaField.PropertyData);
        if (created == null) return null;

        created.ResolveAncestries(asset, (AncestryInfo)ancestry.Clone());
        created.IsZero = true;
        return created;
    }

    private static UsmapProperty? TryFindField(UAsset asset, FName name, AncestryInfo ancestry)
    {
        try
        {
            return asset.Mappings.TryGetProperty<UsmapProperty>(name, ancestry, 0, asset, out var field, out _) ? field : null;
        }
        catch (FormatException)
        {
            // The owner (or one of its bases) has no schema at all, e.g. a natively serialized Vector.
            return null;
        }
    }

    private static PropertyData? Build(UAsset asset, FName name, UsmapPropertyData schema)
    {
        switch (schema)
        {
            case UsmapStructData structSchema:
                var structType = new FName(asset, structSchema.StructType);
                var inner = TryBuildNative(asset, structType, name);
                return new StructPropertyData(name, structType) { Value = inner != null ? [inner] : [] };

            case UsmapEnumData enumSchema:
                return new EnumPropertyData(name)
                {
                    EnumType = new FName(asset, enumSchema.Name),
                    InnerType = enumSchema.InnerType != null ? new FName(asset, enumSchema.InnerType.Type.ToString()) : null,
                };

            case UsmapArrayData arraySchema when schema.Type == UsmapPropertyType.ArrayProperty:
                return new ArrayPropertyData(name) { ArrayType = new FName(asset, arraySchema.InnerType.Type.ToString()), Value = [] };

            case UsmapArrayData or UsmapMapData:
                return null;

            default:
                return TryBuildNative(asset, new FName(asset, schema.Type.ToString()), name);
        }
    }

    /// <summary>A registered property type (a scalar, or a natively serialized struct like Vector); null for a plain struct.</summary>
    private static PropertyData? TryBuildNative(UAsset asset, FName type, FName name)
    {
        try
        {
            var built = MainSerializer.TypeToClass(type, name, null, null, null, asset, isZero: true);
            return built is StructPropertyData ? null : built;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
