using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetEditor.Core.Editing;

/// <summary>
/// Turns a versioned (tagged-property) package into an unversioned one laid out by a game's own schema.
/// A stock editor cooks unversioned properties by its own class layouts; a game that changed an engine
/// class (e.g. added fields to StreamableRenderAsset) then misreads every property. Cooking versioned and
/// converting here keeps each value matched by name, and native data after the properties is untouched.
/// </summary>
public static class UnversionedConverter
{
    /// <summary>
    /// Attaches <paramref name="schema"/>, drops every property it doesn't know (a field only the stock engine
    /// has) and flags the package unversioned. Returns the dropped properties as "Export.Path" strings.
    /// </summary>
    public static IReadOnlyList<string> Convert(UAsset asset, Usmap schema)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(schema);
        if (asset.HasUnversionedProperties) throw new ArgumentException("The package already uses unversioned properties.", nameof(asset));

        asset.Mappings = schema;
        var dropped = new List<string>();
        foreach (var export in asset.Exports.OfType<NormalExport>().Where(e => e.Data != null))
        {
            export.ResolveAncestries(asset, new AncestryInfo());
            Prune(asset, export.Data, export.ObjectName.ToString(), container: null, dropped);
        }

        asset.PackageFlags |= EPackageFlags.PKG_UnversionedProperties;
        asset.IsUnversioned = true;
        return dropped;
    }

    private static void Prune(UAsset asset, List<PropertyData> properties, string path, FName? container, List<string> dropped)
    {
        for (var i = properties.Count - 1; i >= 0; i--)
        {
            var property = properties[i];
            var here = $"{path}.{property.Name}";
            // UAssetAPI holds a natively serialized struct's value (PerPlatformFloat, sampling data) as one child
            // named after its container; that is the payload, not a field the schema would list.
            if (container != null && property.Name == container) continue;
            if (!asset.Mappings.TryGetProperty<UsmapProperty>(property.Name, property.Ancestry, property.ArrayIndex, asset, out _, out _))
            {
                dropped.Add(here);
                properties.RemoveAt(i);
                continue;
            }
            property = RetypeByteEnum(asset, property);
            properties[i] = property;
            MatchEnumSpelling(asset, property, here);
            PruneChildren(asset, property, here, dropped);
        }
    }

    /// <summary>
    /// A tagged cook writes TEnumAsByte as a ByteProperty holding a name; unversioned data stores the enum's index,
    /// which only EnumPropertyData knows how to write. Other properties pass through unchanged.
    /// </summary>
    private static PropertyData RetypeByteEnum(UAsset asset, PropertyData property)
    {
        if (property is not BytePropertyData { ByteType: BytePropertyType.FName, EnumValue: { } value } byteProperty) return property;
        if (!asset.Mappings.TryGetPropertyData(property.Name, property.Ancestry, asset, out UsmapEnumData data)) return property;
        return new EnumPropertyData(byteProperty.Name)
        {
            Ancestry = byteProperty.Ancestry,
            ArrayIndex = byteProperty.ArrayIndex,
            EnumType = new FName(asset, data.Name),
            InnerType = new FName(asset, data.InnerType?.Type.ToString() ?? "ByteProperty"),
            Value = value,
        };
    }

    /// <summary>A tagged cook spells values "EEnum::Value"; the game's schema may list them without the prefix, or with it.</summary>
    private static void MatchEnumSpelling(UAsset asset, PropertyData property, string path)
    {
        if (property is not EnumPropertyData { Value: { } value } enumProperty) return;
        if (!asset.Mappings.TryGetPropertyData(property.Name, property.Ancestry, asset, out UsmapEnumData data)) return;
        if (!asset.Mappings.EnumMap.TryGetValue(data.Name, out var schemaEnum)) return;

        var names = schemaEnum.Values.Values.ToHashSet(StringComparer.Ordinal);
        var cooked = value.ToString();
        var bare = cooked[(cooked.LastIndexOf("::", StringComparison.Ordinal) is var at and >= 0 ? at + 2 : 0)..];
        var match = new[] { cooked, bare, $"{data.Name}::{bare}" }.FirstOrDefault(names.Contains)
            ?? throw new ArgumentException($"{path} = {cooked}: the game's {data.Name} has no such value ({string.Join(", ", names.Order(StringComparer.Ordinal))}).");
        if (match != cooked) enumProperty.Value = new FName(asset, match);
    }

    private static void PruneChildren(UAsset asset, PropertyData property, string path, List<string> dropped)
    {
        switch (property)
        {
            case StructPropertyData { Value: { } fields }:
                Prune(asset, fields, path, property.Name, dropped);
                break;
            case ArrayPropertyData { Value: { } elements }:
                // Elements carry the array's own name, so only their struct fields are checked, not the elements.
                for (var i = 0; i < elements.Length; i++)
                    if (elements[i] is StructPropertyData { Value: { } elementFields })
                        Prune(asset, elementFields, $"{path}[{i}]", elements[i].Name, dropped);
                break;
        }
    }
}
