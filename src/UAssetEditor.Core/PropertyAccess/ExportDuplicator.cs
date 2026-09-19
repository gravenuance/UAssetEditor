using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// Deep-clones a whole export - not an array element within one - and appends it to the
/// asset's export list. Needed for object types whose "children" are separate top-level
/// exports referenced by index rather than PropertyData array elements: a PhysicsAsset's
/// SkeletalBodySetup/PhysicsConstraintTemplate bodies, for instance, each their own export,
/// referenced from the PhysicsAsset's own array properties via ObjectPropertyData/FPackageIndex.
///
/// UAssetAPI's own <see cref="Export.Clone"/> only shallow-copies via MemberwiseClone, so a
/// <see cref="NormalExport.Data"/> list would otherwise be shared with the source - editing the
/// clone's BoneName would silently also edit the original's. This fixes that up with a proper
/// deep clone of every PropertyData element, the same way <see cref="ArrayElementEditor"/> does
/// for a single array slot.
/// </summary>
public static class ExportDuplicator
{
    /// <summary>
    /// Deep-clones the <see cref="NormalExport"/> at <paramref name="sourceIndex"/>, gives the
    /// clone a name distinct from every other export sharing its Outer (same base string, next
    /// free <see cref="FName.Number"/> - Unreal requires sibling object names to be unique), and
    /// appends it to the asset. Returns the new export's index.
    /// </summary>
    public static int Duplicate(UAsset asset, int sourceIndex)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (sourceIndex < 0 || sourceIndex >= asset.Exports.Count || asset.Exports[sourceIndex] is not NormalExport source || source.Data is null)
            throw new ArgumentException("Not a valid NormalExport with a plain top-level property list (e.g. a DataTableExport's real content lives elsewhere and isn't supported here).", nameof(sourceIndex));

        var clone = (NormalExport)source.Clone();
        clone.Data = [.. source.Data.Select(p => (PropertyData)p.Clone())];
        clone.ObjectName = NextAvailableSiblingName(asset, source.ObjectName, source.OuterIndex);

        asset.Exports.Add(clone);
        return asset.Exports.Count - 1;
    }

    private static FName NextAvailableSiblingName(UAsset asset, FName baseName, FPackageIndex? outer)
    {
        // OuterIndex is only populated by Export.Read() on a real, parsed file - null here (a
        // hand-built export, e.g. in tests) just means "no outer", which is itself a valid value
        // to match siblings against, not something to crash on.
        var outerRawIndex = outer?.Index ?? 0;
        var baseString = baseName.Value?.Value;
        var next = asset.Exports
            .Where(e => (e.OuterIndex?.Index ?? 0) == outerRawIndex && string.Equals(e.ObjectName.Value?.Value, baseString, StringComparison.Ordinal))
            .Select(e => e.ObjectName.Number)
            .DefaultIfEmpty(-1)
            .Max() + 1;

        return new FName(asset, baseString, next);
    }
}
