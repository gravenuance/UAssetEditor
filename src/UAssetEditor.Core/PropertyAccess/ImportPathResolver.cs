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
}
