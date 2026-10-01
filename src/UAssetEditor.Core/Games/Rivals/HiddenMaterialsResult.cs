namespace UAssetEditor.Core.Games.Rivals;

/// <summary>The per-LOD hidden-material flags read from a mesh's carrier export, plus a log of what was found.</summary>
internal sealed class HiddenMaterialsResult
{
    public List<bool[]> PerLodFlags { get; } = [];

    public bool FoundUserData { get; set; }

    public List<string> Diagnostics { get; } = [];
}
