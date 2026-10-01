namespace UAssetEditor.Core.Games.Rivals;

/// <summary>
/// What the Marvel Rivals asset patches do. Ported from repak-rivals' <c>KawaiiPhysicsPortOptions</c>, keeping only
/// the switches its packing path uses (the fork's per-field tuning overrides are not carried over).
/// </summary>
public sealed record KawaiiPhysicsPortOptions
{
    /// <summary>Rebuild legacy <c>AnimNode_KawaiiPhysics</c> nodes into the game's <c>Chains</c> layout.</summary>
    public bool PatchKawaiiPhysics { get; init; } = true;

    /// <summary>
    /// Rebuild chain 0 of a node that already has <c>Chains</c> instead of skipping it. repak-rivals' own commands
    /// always set this; leaving it off makes an already-ported node count as a skipped existing chain.
    /// </summary>
    public bool ForceRebuildChain0 { get; init; }

    /// <summary>Fill <c>LODInfo[].DefaultHiddenMaterials</c> from the mesh's hidden-materials carrier data.</summary>
    public bool PatchDefaultHiddenMaterials { get; init; }

    /// <summary>
    /// One bitmap per LOD (bit n set = material slot n hidden) that replaces <see cref="PatchDefaultHiddenMaterials"/>'s
    /// carrier lookup. A single bitmap applies to every LOD. Null or empty means no override.
    /// </summary>
    public IReadOnlyList<ulong>? DefaultHiddenMaterialBitmaps { get; init; }

    internal bool HasBitmapOverride => DefaultHiddenMaterialBitmaps is { Count: > 0 };
}
