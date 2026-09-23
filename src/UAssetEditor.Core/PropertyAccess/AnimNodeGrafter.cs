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
        foreach (var node in donorNodes)
        {
            from.IndexOf(node);
            from.SingleInputOf(node);
            RequireSameFoldedConstants(from, to, RowOf(from, node));
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
            to.NodeData.Value = [.. to.NodeData.Value ?? [], row];

            AddNodeTypeIfMissing(copier, from, to, fromTypes, toTypes, donorName);

            grafted.Add(new GraftedAnimNode(donorName, name, newIndex));
            previous = newIndex;
        }

        rootInput.Value = previous;
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

    /// <summary>A row's entries index the class's folded-constant struct; they only carry over if that slot means the same in both.</summary>
    private static void RequireSameFoldedConstants(AnimGraph from, AnimGraph to, StructPropertyData row)
    {
        var entries = row.Value.OfType<ArrayPropertyData>().FirstOrDefault(p => FNameDisplay.ToDisplayString(p.Name) == "Entries")?.Value ?? [];
        var fromMembers = ConstantMembers(from.Asset);
        var toMembers = ConstantMembers(to.Asset);
        foreach (var entry in entries.OfType<UInt32PropertyData>().Select(e => e.Value).Where(v => v != UnfoldedEntry).Distinct())
        {
            if (entry >= fromMembers.Count || entry >= toMembers.Count || fromMembers[(int)entry] != toMembers[(int)entry])
                throw new InvalidOperationException($"Folded constant #{entry} differs between donor and target; this node can't be copied as is.");
        }
    }

    private static List<string> ConstantMembers(UAsset asset)
    {
        var constants = asset.Exports.OfType<StructExport>().FirstOrDefault(e => e is not ClassExport && e.ObjectName.ToString() == ConstantDataName)
            ?? throw new InvalidOperationException($"No {ConstantDataName} in {asset.FilePath}.");
        // "__StructProperty_12" and "__StructProperty_114" are the same slot; only the compile-order suffix differs.
        return constants.LoadedProperties.Select(p => $"{p.SerializedType?.Value?.Value}:{StripNumber(FNameDisplay.ToDisplayString(p.Name))}").ToList();
    }

    private static string StripNumber(string name)
    {
        var underscore = name.LastIndexOf('_');
        return underscore > 0 && int.TryParse(name.AsSpan(underscore + 1), out _) ? name[..underscore] : name;
    }
}
