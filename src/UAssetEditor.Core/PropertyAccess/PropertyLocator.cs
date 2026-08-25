using UAssetAPI;
using UAssetAPI.ExportTypes;

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
}
