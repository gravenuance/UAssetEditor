namespace UAssetEditor.Core.Games.Rivals;

/// <summary>What one asset's patch did, in the four counts repak-rivals reports.</summary>
public sealed class KawaiiPhysicsPortResult
{
    /// <summary>KawaiiPhysics anim nodes found.</summary>
    public int VisitedAnimNodes { get; internal set; }

    /// <summary>Anim nodes rewritten into the game's layout.</summary>
    public int PortedAnimNodes { get; internal set; }

    /// <summary>Anim nodes left alone because they already had chains (only when chain 0 is not force-rebuilt).</summary>
    public int SkippedExistingChains { get; internal set; }

    /// <summary>LOD entries that received a <c>DefaultHiddenMaterials</c> array.</summary>
    public int PatchedDefaultHiddenMaterialLods { get; internal set; }

    /// <summary>True when the asset needs writing back.</summary>
    public bool Changed => PortedAnimNodes > 0 || PatchedDefaultHiddenMaterialLods > 0;
}
