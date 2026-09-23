using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>Where <see cref="AnimNodeSplicer.SpliceAfter"/> put the new node and which link now reads from it.</summary>
public sealed record SplicedAnimNode(string Name, int NodeIndex, string TemplateName, int TemplateIndex, string ConsumerName);

/// <summary>
/// Adds a copy of an anim-graph node to a compiled Animation Blueprint and wires it into the pose
/// chain directly after its template: template -> copy -> whatever used to read the template.
/// A node's index is its position among the class's AnimNode_* members, and every PoseLink.LinkID
/// holds the index of the node feeding it.
/// </summary>
public static class AnimNodeSplicer
{
    private const string AnimNodeDataName = "AnimNodeData";

    public static SplicedAnimNode SpliceAfter(UAsset asset, int cdoExportIndex, string templateName)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(templateName);

        if (cdoExportIndex < 0 || cdoExportIndex >= asset.Exports.Count || asset.Exports[cdoExportIndex] is not NormalExport cdo)
            throw new ArgumentException("Not a valid export.", nameof(cdoExportIndex));
        var classExport = ClassPropertyDeclarer.FindOwningClass(asset, cdoExportIndex)
            ?? throw new InvalidOperationException("No class in this asset declares this export as its ClassDefaultObject.");

        // Every check runs before the first change, so a refused splice leaves the asset untouched.
        var nodeNames = NodeNames(asset, classExport);
        var nodeData = classExport.Data?.OfType<ArrayPropertyData>().FirstOrDefault(p => FNameDisplay.ToDisplayString(p.Name) == AnimNodeDataName)
            ?? throw new InvalidOperationException("This class has no AnimNodeData table.");
        if ((nodeData.Value?.Length ?? 0) != nodeNames.Count)
            throw new InvalidOperationException($"AnimNodeData has {nodeData.Value?.Length ?? 0} rows but the class declares {nodeNames.Count} nodes.");

        var templateIndex = nodeNames.IndexOf(templateName);
        if (templateIndex < 0)
            throw new InvalidOperationException($"'{templateName}' isn't an anim-graph node on this class.");

        var templateValue = cdo.Data.First(p => FNameDisplay.ToDisplayString(p.Name) == templateName);
        if (PoseLinksIn(templateValue).Count != 1)
            throw new InvalidOperationException($"'{templateName}' must have exactly one pose input to splice after.");

        var nodeSet = nodeNames.ToHashSet(StringComparer.Ordinal);
        var consumers = cdo.Data
            .Where(p => nodeSet.Contains(FNameDisplay.ToDisplayString(p.Name)))
            .SelectMany(node => PoseLinksIn(node).Select(link => (Node: node, Link: link)))
            .Where(x => x.Link.Value == templateIndex)
            .ToList();
        if (consumers.Count != 1)
            throw new InvalidOperationException($"'{templateName}' feeds {consumers.Count} pose inputs; splicing needs exactly one.");

        var (newName, newValue) = ClassPropertyDeclarer.DeclareClonedProperty(asset, cdoExportIndex, templateName);
        var newIndex = nodeNames.Count;

        PoseLinksIn(newValue)[0].Value = templateIndex;
        var row = (StructPropertyData)ArrayElementEditor.Duplicate(nodeData, templateIndex);
        row.Value.OfType<IntPropertyData>().Single(p => FNameDisplay.ToDisplayString(p.Name) == "NodeIndex").Value = newIndex;
        consumers[0].Link.Value = newIndex;

        return new SplicedAnimNode(newName, newIndex, templateName, templateIndex, FNameDisplay.ToDisplayString(consumers[0].Node.Name));
    }

    /// <summary>The class's anim-graph nodes in node-index order.</summary>
    public static IReadOnlyList<string> AnimNodeNames(UAsset asset, ClassExport classExport)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(classExport);
        return NodeNames(asset, classExport);
    }

    private static List<string> NodeNames(UAsset asset, ClassExport classExport) =>
        classExport.LoadedProperties
            .OfType<FStructProperty>()
            .Where(p => StructName(asset, p).StartsWith("AnimNode_", StringComparison.Ordinal))
            .Select(p => FNameDisplay.ToDisplayString(p.Name))
            .ToList();

    private static string StructName(UAsset asset, FStructProperty property) =>
        property.Struct.IsImport() ? property.Struct.ToImport(asset).ObjectName.ToString()
        : property.Struct.IsExport() ? asset.Exports[property.Struct.Index - 1].ObjectName.ToString()
        : "";

    /// <summary>Every LinkID inside a node value: its own pose inputs, including ones held in arrays.</summary>
    private static List<IntPropertyData> PoseLinksIn(PropertyData node)
    {
        var links = new List<IntPropertyData>();
        Collect(node);
        return links;

        void Collect(PropertyData property)
        {
            switch (property)
            {
                case StructPropertyData s when s.StructType?.ToString().EndsWith("PoseLink", StringComparison.Ordinal) == true:
                    links.AddRange(s.Value.OfType<IntPropertyData>().Where(p => FNameDisplay.ToDisplayString(p.Name) == "LinkID"));
                    break;
                case StructPropertyData s:
                    foreach (var child in s.Value) Collect(child);
                    break;
                case ArrayPropertyData a:
                    foreach (var element in a.Value ?? []) Collect(element);
                    break;
            }
        }
    }
}
