using UAssetAPI;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>A node tag of a compiled Animation Blueprint: its end-counted index and the node that index names.</summary>
public sealed record AnimNodeTag(string Tag, int Index, string Node);

/// <summary>
/// Reads and re-points the node tags in a compiled Animation Blueprint's sparse class data
/// (AnimBlueprintExtension_Tag.NodeIndices). They count from the end of the node list, so a node added by an
/// older tool can leave one naming the wrong node; the game's own copy, moved past the added nodes, repairs it.
/// </summary>
public static class AnimNodeTags
{
    public static IReadOnlyList<AnimNodeTag> List(UAsset asset, int cdoExportIndex)
    {
        var graph = AnimGraph.Open(asset, cdoExportIndex);
        return graph.Tags().Select(t => new AnimNodeTag(t.Tag, t.Index.Value, graph.NodeNames[graph.NodeNames.Count - 1 - t.Index.Value])).ToList();
    }

    /// <summary>Points <paramref name="tag"/> at end-counted node <paramref name="index"/>; refuses an unknown tag or an index past the nodes.</summary>
    public static AnimNodeTag Set(UAsset asset, int cdoExportIndex, string tag, int index)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var graph = AnimGraph.Open(asset, cdoExportIndex);
        var entries = graph.Tags().Where(t => t.Tag == tag).ToList();
        if (entries.Count != 1)
            throw new InvalidOperationException($"The class has no node tag '{tag}'.");
        if (index < 0 || index >= graph.NodeNames.Count)
            throw new InvalidOperationException($"Node index {index} is past the class's {graph.NodeNames.Count} nodes.");

        entries[0].Index.Value = index;
        graph.SaveSparseData();
        return new AnimNodeTag(tag, index, graph.NodeNames[graph.NodeNames.Count - 1 - index]);
    }
}
