using UAssetEditor.Core.Games.Rivals;

namespace UAssetEditor.Tooling;

/// <summary>The Marvel Rivals asset patches an IoStore pack can apply.</summary>
internal static class RivalsPatches
{
    /// <summary>The patch options for the requested switches, or null when none is requested. The caller checks the game and the usmap.</summary>
    /// <param name="bitmapsText">Comma-separated LOD masks (e.g. <c>0x5,0x1</c>) to set instead of reading them from the mesh; null for none.</param>
    public static KawaiiPhysicsPortOptions? Create(bool kawaiiPhysics, bool defaultHiddenMats, string? bitmapsText)
    {
        if (!kawaiiPhysics && !defaultHiddenMats && bitmapsText == null) return null;

        IReadOnlyList<ulong>? bitmaps;
        try
        {
            bitmaps = bitmapsText == null ? null : DefaultHiddenMaterialBitmaps.Parse(bitmapsText);
        }
        catch (FormatException ex)
        {
            throw new ArgException(ex.Message, ex);
        }

        return new KawaiiPhysicsPortOptions
        {
            PatchKawaiiPhysics = kawaiiPhysics,
            // As repak-rivals' own commands do, so an already-ported node is rebuilt instead of skipped.
            ForceRebuildChain0 = true,
            PatchDefaultHiddenMaterials = defaultHiddenMats,
            DefaultHiddenMaterialBitmaps = bitmaps,
        };
    }
}
