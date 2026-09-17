using UAssetAPI;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// Reconstructs a dotted path for an <see cref="Import"/> by walking its Outer chain,
/// e.g. "/Game/Textures/T_Wall.T_Wall" style names collapse to "T_Wall.T_Wall" here
/// since UAssetAPI stores the package path separately on the outermost import.
/// </summary>
public static class ImportPathResolver
{
    public static string GetFullPath(Import import, UAsset asset)
    {
        ArgumentNullException.ThrowIfNull(import);

        var parts = new List<string> { import.ObjectName.Value?.Value ?? "" };

        var outer = import.OuterIndex;
        while (!outer.IsNull() && outer.IsImport())
        {
            var outerImport = outer.ToImport(asset);
            parts.Insert(0, outerImport.ObjectName.Value?.Value ?? "");
            outer = outerImport.OuterIndex;
        }

        return string.Join(".", parts);
    }

    /// <summary>Finds the 0-based import index whose <see cref="GetFullPath"/> matches <paramref name="fullPath"/> exactly, or null if none does.</summary>
    public static int? FindImportIndex(UAsset asset, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(fullPath);

        for (var i = 0; i < asset.Imports.Count; i++)
        {
            if (GetFullPath(asset.Imports[i], asset) == fullPath)
                return i;
        }
        return null;
    }
}
