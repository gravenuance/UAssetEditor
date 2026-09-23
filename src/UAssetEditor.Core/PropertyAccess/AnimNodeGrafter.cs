using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>One node <see cref="AnimNodeGrafter.GraftBeforeRoot"/> added: its name in the target and its node index.</summary>
public sealed record GraftedAnimNode(string DonorName, string Name, int NodeIndex);

/// <summary>
/// Copies anim-graph nodes from another compiled Animation Blueprint and wires them in as a chain
/// just before the target's Root: [whatever Root read] -> copies in order -> Root. Each copy brings
/// its class member, default value, AnimNodeData row and NodeTypeMap entry, with names and object
/// references re-created in the target (<see cref="CrossAssetCopier"/>).
/// </summary>
public static class AnimNodeGrafter
{
    private const uint UnfoldedEntry = uint.MaxValue;
    private const string ConstantDataName = "AnimBlueprintGeneratedConstantData";

    public static IReadOnlyList<GraftedAnimNode> GraftBeforeRoot(UAsset target, int targetCdoIndex, UAsset donor, int donorCdoIndex, IReadOnlyList<string> donorNodes)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(donor);
        ArgumentNullException.ThrowIfNull(donorNodes);
        if (donorNodes.Count == 0) throw new ArgumentException("Name at least one donor node.", nameof(donorNodes));

        // Every check runs before the first change, so a refused graft leaves the target untouched.
        var to = AnimGraph.Open(target, targetCdoIndex);
        var from = AnimGraph.Open(donor, donorCdoIndex);
        var toTypes = to.NodeTypeMap ?? throw new InvalidOperationException("The target class has no NodeTypeMap.");
        var fromTypes = from.NodeTypeMap ?? throw new InvalidOperationException("The donor class has no NodeTypeMap.");
        var rootInput = to.SingleInputOf(to.SingleNodeOfType("AnimNode_Root"));
        var constantMap = FoldedConstantMap(donor, target);
        foreach (var node in donorNodes)
        {
            from.IndexOf(node);
            from.SingleInputOf(node);
            foreach (var entry in EntriesOf(RowOf(from, node)).Where(e => e.Value != UnfoldedEntry && !constantMap.ContainsKey(e.Value)))
                throw new InvalidOperationException($"'{node}' uses folded constant #{entry.Value}, which the target has no single match for.");
        }

        var copier = new CrossAssetCopier(donor, target);
        copier.MapExport(from.ClassIndex, to.ClassIndex);
        var siblingAncestry = (AncestryInfo)to.Cdo.Data[0].Ancestry.Clone();
        var grafted = new List<GraftedAnimNode>();
        var previous = rootInput.Value;

        foreach (var donorName in donorNodes)
        {
            var declaration = from.DeclarationOf(donorName);
            var (baseName, number) = ClassPropertyDeclarer.NextAvailableName(donorName, to.Class.LoadedProperties.Select(p => FNameDisplay.ToDisplayString(p.Name)));
            var newIndex = to.NodeNames.Count;

            to.Class.LoadedProperties = [.. to.Class.LoadedProperties, new FStructProperty
            {
                Name = new FName(target, baseName, number + 1),
                SerializedType = copier.MapName(declaration.SerializedType),
                Flags = declaration.Flags,
                MetaDataMap = new TMap<FName, FString>(),
                ArrayDim = declaration.ArrayDim,
                ElementSize = declaration.ElementSize,
                PropertyFlags = declaration.PropertyFlags,
                RepIndex = declaration.RepIndex,
                RepNotifyFunc = copier.MapName(declaration.RepNotifyFunc),
                BlueprintReplicationCondition = declaration.BlueprintReplicationCondition,
                Struct = copier.MapIndex(declaration.Struct),
            }];
            var name = $"{baseName}_{number}";
            to.NodeNames.Add(name);

            var value = copier.Copy(from.ValueOf(donorName));
            value.Name = new FName(target, baseName, number + 1);
            value.ResolveAncestries(target, (AncestryInfo)siblingAncestry.Clone());
            AnimGraph.PoseLinksIn(value)[0].Value = previous;
            to.Cdo.Data.Add(value);

            var row = (StructPropertyData)copier.Copy(RowOf(from, donorName));
            AnimNodeSplicer.SetNodeIndex(row, newIndex);
            foreach (var entry in EntriesOf(row).Where(e => e.Value != UnfoldedEntry)) entry.Value = constantMap[entry.Value];
            to.NodeData.Value = [.. to.NodeData.Value ?? [], row];

            AddNodeTypeIfMissing(copier, from, to, fromTypes, toTypes, donorName);
            to.AddEmptyHandler();

            grafted.Add(new GraftedAnimNode(donorName, name, newIndex));
            previous = newIndex;
        }

        rootInput.Value = previous;
        to.SaveSparseData();
        to.NodeData.ResolveAncestries(target, to.NodeData.Ancestry);
        toTypes.ResolveAncestries(target, toTypes.Ancestry);
        target.RegisterStructSchema(to.Class);
        return grafted;
    }

    private static StructPropertyData RowOf(AnimGraph graph, string nodeName) => (StructPropertyData)graph.NodeData.Value[graph.IndexOf(nodeName)];

    private static void AddNodeTypeIfMissing(CrossAssetCopier copier, AnimGraph from, AnimGraph to, MapPropertyData fromTypes, MapPropertyData toTypes, string donorName)
    {
        var structPath = TypeKey(from.Asset, from.DeclarationOf(donorName).Struct);
        if (toTypes.Value.Keys.Any(k => TypeKey(to.Asset, k) == structPath)) return;

        var entry = fromTypes.Value.FirstOrDefault(e => TypeKey(from.Asset, e.Key) == structPath);
        if (entry.Key == null) throw new InvalidOperationException($"The donor has no NodeTypeMap entry for {structPath}.");
        toTypes.Value.Add(copier.Copy(entry.Key), copier.Copy(entry.Value));
    }

    private static string TypeKey(UAsset asset, PropertyData key) => key is ObjectPropertyData o ? TypeKey(asset, o.Value) : "";

    private static string TypeKey(UAsset asset, FPackageIndex index) =>
        index.IsImport() ? ImportPathResolver.GetFullPath(index.ToImport(asset), asset) : "";

    private static IEnumerable<UInt32PropertyData> EntriesOf(StructPropertyData row) =>
        row.Value.OfType<ArrayPropertyData>().FirstOrDefault(p => FNameDisplay.ToDisplayString(p.Name) == "Entries")?.Value?.OfType<UInt32PropertyData>() ?? [];

    /// <summary>
    /// A row's entries index the class's folded-constant struct, whose member order differs between
    /// blueprints. Donor slot -> target slot of the same kind, for kinds the target holds exactly once.
    /// </summary>
    private static Dictionary<uint, uint> FoldedConstantMap(UAsset donor, UAsset target)
    {
        var fromMembers = ConstantMembers(donor);
        var toMembers = ConstantMembers(target);
        var map = new Dictionary<uint, uint>();
        for (var i = 0; i < fromMembers.Count; i++)
        {
            var matches = Enumerable.Range(0, toMembers.Count).Where(j => toMembers[j] == fromMembers[i]).ToList();
            if (matches.Count == 1) map[(uint)i] = (uint)matches[0];
        }
        return map;
    }

    private static List<string> ConstantMembers(UAsset asset)
    {
        var constants = asset.Exports.OfType<StructExport>().FirstOrDefault(e => e is not ClassExport && e.ObjectName.ToString() == ConstantDataName)
            ?? throw new InvalidOperationException($"No {ConstantDataName} in {asset.FilePath}.");
        // "__StructProperty_12" and "__StructProperty_114" are the same slot; only the compile-order suffix differs.
        // A struct constant is identified by the struct it holds; several different ones can share the "__StructProperty" name.
        return constants.LoadedProperties.Select(p =>
            $"{p.SerializedType?.Value?.Value}:{StripNumber(FNameDisplay.ToDisplayString(p.Name))}:{(p is FStructProperty s ? AnimGraph.StructName(asset, s.Struct) : "")}").ToList();
    }

    private static string StripNumber(string name)
    {
        var underscore = name.LastIndexOf('_');
        return underscore > 0 && int.TryParse(name.AsSpan(underscore + 1), out _) ? name[..underscore] : name;
    }
}
