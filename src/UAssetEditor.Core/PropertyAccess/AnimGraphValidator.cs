using UAssetAPI;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// Checks a compiled Animation Blueprint the way the game will use it: one node-table row and one exposed-value
/// handler per declared node, and cached-pose and asset-player indices that still name the right nodes. A mismatch
/// makes the engine read past an array or run the wrong node at runtime.
/// </summary>
public static class AnimGraphValidator
{
    /// <summary>The number of nodes, or <see cref="InvalidOperationException"/> naming what disagrees.</summary>
    public static int Validate(UAsset asset, int cdoExportIndex)
    {
        var graph = AnimGraph.Open(asset, cdoExportIndex);
        graph.CheckSavedPoseIndices();
        graph.CheckAssetPlayerIndices();
        return graph.NodeNames.Count;
    }
}
