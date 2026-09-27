using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// A compiled Animation Blueprint's node graph as stored: a node's index is its position among the
/// class's AnimNode_* members, every PoseLink.LinkID holds the index of the node feeding it, and the
/// class's AnimNodeData table has one row per node, as does the exposed-value handler list in its
/// sparse data. The engine indexes that list by node index unchecked, so a node without a handler
/// reads past its end. Two class tables count the other way, from the end of the node list: the
/// cached-pose update order and the asset players. Adding a node moves every one of those by one,
/// and the engine trusts them unchecked too, so a stale one runs another node as a cached pose.
/// </summary>
internal sealed class AnimGraph
{
    private const string AnimNodeDataName = "AnimNodeData";
    private const string NodeTypeMapName = "NodeTypeMap";
    private const string HandlersPath = "AnimBlueprintExtension_Base.ExposedValueHandlers";
    private const string SavedPoseTable = "OrderedSavedPoseIndicesMap";
    private const string AssetPlayerTable = "GraphAssetPlayerInformation";
    private const string SaveCachedPoseStruct = "AnimNode_SaveCachedPose";
    private const string AssetPlayerRelevancyBase = "AnimNode_AssetPlayerRelevancyBase";

    private readonly SparseClassData? _sparseData;

    private AnimGraph(UAsset asset, int cdoIndex, NormalExport cdo, int classIndex, ClassExport classExport, List<string> nodeNames, ArrayPropertyData nodeData, SparseClassData? sparseData)
    {
        Asset = asset;
        CdoIndex = cdoIndex;
        Cdo = cdo;
        ClassIndex = classIndex;
        Class = classExport;
        NodeNames = nodeNames;
        NodeData = nodeData;
        _sparseData = sparseData;
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
        var sparseData = SparseClassData.Read(asset, cdo);
        var handlers = sparseData?.Find<ArrayPropertyData>(HandlersPath);
        if (handlers != null && (handlers.Value?.Length ?? 0) != nodeNames.Count)
            throw new InvalidOperationException($"The class has {handlers.Value?.Length ?? 0} exposed-value handlers but declares {nodeNames.Count} nodes.");

        var graph = new AnimGraph(asset, cdoExportIndex, cdo, asset.Exports.IndexOf(classExport), classExport, nodeNames, nodeData, sparseData);
        var outOfRange = graph.EndCountedIndices().FirstOrDefault(i => i.Value < 0 || i.Value >= nodeNames.Count);
        if (outOfRange != null)
            throw new InvalidOperationException($"{FNameDisplay.ToDisplayString(outOfRange.Name)} holds {outOfRange.Value}, past the class's {nodeNames.Count} nodes.");
        return graph;
    }

    /// <summary>Every node index in the tables that count from the end of the node list (cached poses, asset players).</summary>
    public IEnumerable<IntPropertyData> EndCountedIndices() => EndCountedIndicesIn(SavedPoseTable).Concat(EndCountedIndicesIn(AssetPlayerTable));

    /// <summary>Call after appending <paramref name="added"/> nodes: the same nodes are now that much further from the end.</summary>
    public void KeepEndCountedIndicesOnTheirNodes(int added)
    {
        foreach (var index in EndCountedIndices()) index.Value += added;
    }

    /// <summary>Refuses a cached-pose index that doesn't name a SaveCachedPose node (the engine would run another node as one).</summary>
    public void CheckSavedPoseIndices()
    {
        foreach (var index in EndCountedIndicesIn(SavedPoseTable))
        {
            var node = NodeNames[NodeNames.Count - 1 - index.Value];
            if (StructNameOf(node) != SaveCachedPoseStruct)
                throw new InvalidOperationException($"Cached-pose index {index.Value} names '{node}', not a SaveCachedPose node: stale after nodes were added.");
        }
    }

    /// <summary>
    /// Refuses an asset-player index whose node the usmap knows is no asset player. Structs the usmap doesn't
    /// describe, or an asset without one, pass: inheritance is the only reliable test, and it needs the usmap.
    /// </summary>
    public void CheckAssetPlayerIndices()
    {
        var schemas = Asset.Mappings?.Schemas;
        if (schemas == null) return;
        foreach (var index in EndCountedIndicesIn(AssetPlayerTable))
        {
            var node = NodeNames[NodeNames.Count - 1 - index.Value];
            if (IsAssetPlayer(schemas, StructNameOf(node)) == false)
                throw new InvalidOperationException($"Asset-player index {index.Value} names '{node}', not an asset player: stale after nodes were added.");
        }
    }

    private static bool? IsAssetPlayer(IDictionary<string, UsmapSchema> schemas, string structName)
    {
        for (var name = structName; !string.IsNullOrEmpty(name);)
        {
            if (name is AssetPlayerRelevancyBase) return true;
            if (!schemas.TryGetValue(name, out var schema)) return name == structName ? null : false;
            name = schema.SuperType;
        }
        return false;
    }

    private IEnumerable<IntPropertyData> EndCountedIndicesIn(string table) =>
        Class.Data?.OfType<MapPropertyData>().Where(m => FNameDisplay.ToDisplayString(m.Name) == table)
            .SelectMany(m => m.Value.Values.OfType<StructPropertyData>())
            .SelectMany(s => s.Value.OfType<ArrayPropertyData>())
            .SelectMany(a => (a.Value ?? []).OfType<IntPropertyData>()) ?? [];

    /// <summary>Gives the newest node an exposed-value handler that does nothing; call <see cref="SaveSparseData"/> once done.</summary>
    public void AddEmptyHandler()
    {
        var handlers = _sparseData?.Find<ArrayPropertyData>(HandlersPath);
        if (handlers == null) return;

        var existing = handlers.Value ?? [];
        var template = existing.OfType<StructPropertyData>().FirstOrDefault(IsEmptyHandler);
        var handler = template != null
            ? (StructPropertyData)template.Clone()
            : new StructPropertyData(handlers.Name, ((StructPropertyData)existing[0]).StructType) { Value = [] };
        handlers.Value = [.. existing, handler];
    }

    public void SaveSparseData() => _sparseData?.Save();

    private static bool IsEmptyHandler(StructPropertyData handler) => handler.Value.All(p => p switch
    {
        ArrayPropertyData a => (a.Value?.Length ?? 0) == 0,
        ObjectPropertyData o => o.Value == null || o.Value.Index == 0,
        NamePropertyData n => n.Value == null || n.Value.ToString() == "None",
        _ => p.IsZero,
    });

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

    public static string StructName(UAsset asset, FPackageIndex? structIndex) =>
        structIndex is null ? ""
        : structIndex.IsImport() ? structIndex.ToImport(asset)?.ObjectName?.ToString() ?? ""
        : structIndex.IsExport() ? asset.Exports[structIndex.Index - 1].ObjectName.ToString()
        : "";
}
