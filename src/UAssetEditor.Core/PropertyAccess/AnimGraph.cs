using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// A compiled Animation Blueprint's node graph as stored: a node's index is its position among the
/// class's AnimNode_* members, every PoseLink.LinkID holds the index of the node feeding it, and the
/// class's AnimNodeData table has one row per node.
/// </summary>
internal sealed class AnimGraph
{
    private const string AnimNodeDataName = "AnimNodeData";
    private const string NodeTypeMapName = "NodeTypeMap";

    private AnimGraph(UAsset asset, int cdoIndex, NormalExport cdo, int classIndex, ClassExport classExport, List<string> nodeNames, ArrayPropertyData nodeData)
    {
        Asset = asset;
        CdoIndex = cdoIndex;
        Cdo = cdo;
        ClassIndex = classIndex;
        Class = classExport;
        NodeNames = nodeNames;
        NodeData = nodeData;
    }

    public UAsset Asset { get; }
    public int CdoIndex { get; }
    public NormalExport Cdo { get; }
    public int ClassIndex { get; }
    public ClassExport Class { get; }
    public List<string> NodeNames { get; }
    public ArrayPropertyData NodeData { get; }

    public MapPropertyData? NodeTypeMap =>
        Class.Data?.OfType<MapPropertyData>().FirstOrDefault(p => FNameDisplay.ToDisplayString(p.Name) == NodeTypeMapName);

    /// <summary>Reads the graph, refusing a class whose node table disagrees with its declared nodes.</summary>
    public static AnimGraph Open(UAsset asset, int cdoExportIndex)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (cdoExportIndex < 0 || cdoExportIndex >= asset.Exports.Count || asset.Exports[cdoExportIndex] is not NormalExport cdo)
            throw new ArgumentException("Not a valid export.", nameof(cdoExportIndex));

        var classExport = ClassPropertyDeclarer.FindOwningClass(asset, cdoExportIndex)
            ?? throw new InvalidOperationException("No class in this asset declares this export as its ClassDefaultObject.");
        var nodeNames = NodeNamesOf(asset, classExport);
        var nodeData = classExport.Data?.OfType<ArrayPropertyData>().FirstOrDefault(p => FNameDisplay.ToDisplayString(p.Name) == AnimNodeDataName)
            ?? throw new InvalidOperationException("This class has no AnimNodeData table.");
        if ((nodeData.Value?.Length ?? 0) != nodeNames.Count)
            throw new InvalidOperationException($"AnimNodeData has {nodeData.Value?.Length ?? 0} rows but the class declares {nodeNames.Count} nodes.");

        return new AnimGraph(asset, cdoExportIndex, cdo, asset.Exports.IndexOf(classExport), classExport, nodeNames, nodeData);
    }

    public static List<string> NodeNamesOf(UAsset asset, ClassExport classExport) =>
        classExport.LoadedProperties
            .OfType<FStructProperty>()
            .Where(p => StructName(asset, p.Struct).StartsWith("AnimNode_", StringComparison.Ordinal))
            .Select(p => FNameDisplay.ToDisplayString(p.Name))
            .ToList();

    public int IndexOf(string nodeName)
    {
        var index = NodeNames.IndexOf(nodeName);
        return index >= 0 ? index : throw new InvalidOperationException($"'{nodeName}' isn't an anim-graph node on this class.");
    }

    public PropertyData ValueOf(string nodeName) => Cdo.Data.First(p => FNameDisplay.ToDisplayString(p.Name) == nodeName);

    public FStructProperty DeclarationOf(string nodeName) =>
        Class.LoadedProperties.OfType<FStructProperty>().First(p => FNameDisplay.ToDisplayString(p.Name) == nodeName);

    public string StructNameOf(string nodeName) => StructName(Asset, DeclarationOf(nodeName).Struct);

    /// <summary>The single node of this struct type, e.g. "AnimNode_Root".</summary>
    public string SingleNodeOfType(string structName)
    {
        var matches = NodeNames.Where(n => StructNameOf(n) == structName).ToList();
        return matches.Count == 1 ? matches[0] : throw new InvalidOperationException($"Expected one {structName} node, found {matches.Count}.");
    }

    /// <summary>The pose input a node reads from, refusing nodes with none or several.</summary>
    public IntPropertyData SingleInputOf(string nodeName)
    {
        var links = PoseLinksIn(ValueOf(nodeName));
        return links.Count == 1 ? links[0] : throw new InvalidOperationException($"'{nodeName}' must have exactly one pose input, has {links.Count}.");
    }

    /// <summary>Every pose input, across all nodes, that reads node <paramref name="index"/>.</summary>
    public List<(string Node, IntPropertyData Link)> ReadersOf(int index)
    {
        var nodeSet = NodeNames.ToHashSet(StringComparer.Ordinal);
        return Cdo.Data
            .Where(p => nodeSet.Contains(FNameDisplay.ToDisplayString(p.Name)))
            .SelectMany(node => PoseLinksIn(node).Select(link => (FNameDisplay.ToDisplayString(node.Name), link)))
            .Where(x => x.link.Value == index)
            .ToList();
    }

    /// <summary>Every LinkID inside a node value: its own pose inputs, including ones held in arrays.</summary>
    public static List<IntPropertyData> PoseLinksIn(PropertyData node)
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

    public static string StructName(UAsset asset, FPackageIndex structIndex) =>
        structIndex.IsImport() ? structIndex.ToImport(asset).ObjectName.ToString()
        : structIndex.IsExport() ? asset.Exports[structIndex.Index - 1].ObjectName.ToString()
        : "";
}
