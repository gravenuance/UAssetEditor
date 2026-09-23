using UAssetAPI;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// Checks a compiled Animation Blueprint the way the game will use it: one node-table row and one exposed-value
/// handler per declared node. A mismatch reads past the end of an engine array at runtime.
/// </summary>
public static class AnimGraphValidator
{
    /// <summary>The number of nodes, or <see cref="InvalidOperationException"/> naming what disagrees.</summary>
    public static int Validate(UAsset asset, int cdoExportIndex) => AnimGraph.Open(asset, cdoExportIndex).NodeNames.Count;
}
