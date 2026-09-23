using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>Where <see cref="AnimNodeSplicer.SpliceAfter"/> put the new node and which link now reads from it.</summary>
public sealed record SplicedAnimNode(string Name, int NodeIndex, string TemplateName, int TemplateIndex, string ConsumerName);

/// <summary>Which node <see cref="AnimNodeSplicer.Bypass"/> took out of the pose chain and what now reads past it.</summary>
public sealed record BypassedAnimNode(string Name, int NodeIndex, string ReaderName, int NowReads);

/// <summary>
/// Edits the pose chain of a compiled Animation Blueprint in place: adds a copy of a node right after
/// it, or takes a node out of the chain. Every check runs before the first change, so a refused edit
/// leaves the asset untouched.
/// </summary>
public static class AnimNodeSplicer
{
    /// <summary>Adds a copy of <paramref name="templateName"/> wired in after it: template -> copy -> whatever read the template.</summary>
    public static SplicedAnimNode SpliceAfter(UAsset asset, int cdoExportIndex, string templateName)
    {
        ArgumentNullException.ThrowIfNull(templateName);
        var graph = AnimGraph.Open(asset, cdoExportIndex);

        var templateIndex = graph.IndexOf(templateName);
        graph.SingleInputOf(templateName);
        var readers = graph.ReadersOf(templateIndex);
        if (readers.Count != 1)
            throw new InvalidOperationException($"'{templateName}' feeds {readers.Count} pose inputs; splicing needs exactly one.");

        var (newName, newValue) = ClassPropertyDeclarer.DeclareClonedProperty(asset, cdoExportIndex, templateName);
        var newIndex = graph.NodeNames.Count;

        AnimGraph.PoseLinksIn(newValue)[0].Value = templateIndex;
        var row = (StructPropertyData)ArrayElementEditor.Duplicate(graph.NodeData, templateIndex);
        SetNodeIndex(row, newIndex);
        readers[0].Link.Value = newIndex;

        return new SplicedAnimNode(newName, newIndex, templateName, templateIndex, readers[0].Node);
    }

    /// <summary>
    /// Takes a node out of the pose chain: whatever read it now reads its input. The node stays in the
    /// class, unreachable, so no other node's index shifts; the engine only evaluates what Root reaches.
    /// </summary>
    public static BypassedAnimNode Bypass(UAsset asset, int cdoExportIndex, string nodeName)
    {
        ArgumentNullException.ThrowIfNull(nodeName);
        var graph = AnimGraph.Open(asset, cdoExportIndex);

        var index = graph.IndexOf(nodeName);
        var input = graph.SingleInputOf(nodeName).Value;
        var readers = graph.ReadersOf(index);
        if (readers.Count != 1)
            throw new InvalidOperationException($"'{nodeName}' feeds {readers.Count} pose inputs; bypassing needs exactly one.");

        readers[0].Link.Value = input;
        return new BypassedAnimNode(nodeName, index, readers[0].Node, input);
    }

    /// <summary>The class's anim-graph nodes in node-index order.</summary>
    public static IReadOnlyList<string> AnimNodeNames(UAsset asset, ClassExport classExport)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(classExport);
        return AnimGraph.NodeNamesOf(asset, classExport);
    }

    internal static void SetNodeIndex(StructPropertyData row, int nodeIndex) =>
        row.Value.OfType<IntPropertyData>().Single(p => FNameDisplay.ToDisplayString(p.Name) == "NodeIndex").Value = nodeIndex;
}
