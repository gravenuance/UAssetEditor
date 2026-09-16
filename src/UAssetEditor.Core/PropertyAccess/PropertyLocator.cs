using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>Re-locates a property previously found by search, by walking the same export fresh each time.</summary>
public static class PropertyLocator
{
    public static PropertyNode? Locate(UAsset asset, int exportIndex, string? propertyPath)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (propertyPath == null || exportIndex < 0 || exportIndex >= asset.Exports.Count) return null;
        if (asset.Exports[exportIndex] is not NormalExport export) return null;
        return PropertyWalker.Walk(export).FirstOrDefault(n => n.Path == propertyPath);
    }

    /// <summary>
    /// Re-locates an array element's own owning array and index by path (e.g. "Chains[3]") -
    /// the counterpart to <see cref="Locate"/> for structural edits (<see cref="ArrayElementEditor"/>),
    /// which need the array itself to mutate, not just the element sitting at that path.
    /// </summary>
    public static (ArrayPropertyData Array, int Index)? LocateArrayElement(UAsset asset, int exportIndex, string elementPath)
    {
        ArgumentNullException.ThrowIfNull(elementPath);

        if (!PropertyPaths.TrySplitTrailingIndex(elementPath, out var parentPath, out var index)) return null;

        var parent = Locate(asset, exportIndex, parentPath);
        return parent?.Property is ArrayPropertyData array ? (array, index) : null;
    }
}
