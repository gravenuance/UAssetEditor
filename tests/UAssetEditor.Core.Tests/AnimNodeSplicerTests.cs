using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests;

public class AnimNodeSplicerTests
{
    // Node indices: Root 0, Input 1, Kawaii 2, ToLocal 3. Pose chain: Input -> Kawaii -> ToLocal -> Root.
    private const string Kawaii = "AnimGraphNode_KawaiiPhysics";

    [Fact]
    public void SpliceAfter_PutsTheCopyBetweenTemplateAndItsConsumer()
    {
        var graph = AnimGraph.Create();

        var spliced = AnimNodeSplicer.SpliceAfter(graph.Asset, graph.CdoIndex, Kawaii);

        Assert.Equal(new SplicedAnimNode("AnimGraphNode_KawaiiPhysics_1", 4, Kawaii, 2, "AnimGraphNode_ToLocal"), spliced);
        Assert.Equal(4, graph.LinkOf("AnimGraphNode_ToLocal"));
        Assert.Equal(2, graph.LinkOf("AnimGraphNode_KawaiiPhysics_1"));
        Assert.Equal(1, graph.LinkOf(Kawaii));
        Assert.Equal([0, 1, 2, 3, 4], graph.NodeDataIndices());
        Assert.Equal(5, AnimNodeSplicer.AnimNodeNames(graph.Asset, graph.Class).Count);
    }

    [Fact]
    public void SpliceAfter_TwiceChainsTheCopies()
    {
        var graph = AnimGraph.Create();

        AnimNodeSplicer.SpliceAfter(graph.Asset, graph.CdoIndex, Kawaii);
        var second = AnimNodeSplicer.SpliceAfter(graph.Asset, graph.CdoIndex, "AnimGraphNode_KawaiiPhysics_1");

        Assert.Equal(5, second.NodeIndex);
        Assert.Equal(5, graph.LinkOf("AnimGraphNode_ToLocal"));
        Assert.Equal(4, graph.LinkOf("AnimGraphNode_KawaiiPhysics_2"));
    }

    [Fact]
    public void SpliceAfter_RefusesANodeNothingReadsFrom_AndChangesNothing()
    {
        var graph = AnimGraph.Create();
        var before = graph.Snapshot();

        Assert.Throws<InvalidOperationException>(() => AnimNodeSplicer.SpliceAfter(graph.Asset, graph.CdoIndex, "AnimGraphNode_Root"));

        Assert.Equal(before, graph.Snapshot());
    }

    [Fact]
    public void SpliceAfter_RefusesWhenTheNodeTableDisagreesWithTheClass_AndChangesNothing()
    {
        var graph = AnimGraph.Create();
        var table = graph.Class.Data.OfType<ArrayPropertyData>().Single();
        table.Value = table.Value[..^1];
        var before = graph.Snapshot();

        Assert.Throws<InvalidOperationException>(() => AnimNodeSplicer.SpliceAfter(graph.Asset, graph.CdoIndex, Kawaii));

        Assert.Equal(before, graph.Snapshot());
    }

    [Fact]
    public void SpliceAfter_RefusesAMemberThatIsNotANode()
    {
        var graph = AnimGraph.Create();

        Assert.Throws<InvalidOperationException>(() => AnimNodeSplicer.SpliceAfter(graph.Asset, graph.CdoIndex, "UberGraphFrame"));
    }

    private sealed record AnimGraph(UAsset Asset, ClassExport Class, NormalExport Cdo, int CdoIndex)
    {
        public static AnimGraph Create()
        {
            var asset = TestAssets.CreateAsset();
            var cdo = new NormalExport(asset, []) { ObjectName = new FName(asset, "Default__Graph_C"), Data = [] };
            asset.Exports.Add(cdo);

            var members = new List<FProperty>();
            void Member(string name, string structType, PropertyData value)
            {
                asset.Imports.Add(new Import("/Script/CoreUObject", "ScriptStruct", FPackageIndex.FromRawIndex(0), structType, false, asset));
                members.Add(new FStructProperty
                {
                    Name = new FName(asset, name),
                    SerializedType = new FName(asset, "StructProperty"),
                    Struct = FPackageIndex.FromImport(asset.Imports.Count - 1),
                });
                cdo.Data.Add(value);
            }

            Member("UberGraphFrame", "PointerToUberGraphFrame", Struct(asset, "UberGraphFrame", "PointerToUberGraphFrame"));
            Member("AnimGraphNode_Root", "AnimNode_Root", Struct(asset, "AnimGraphNode_Root", "AnimNode_Root", Link(asset, "Result", "PoseLink", 3)));
            Member("AnimGraphNode_Input", "AnimNode_LinkedInputPose", Struct(asset, "AnimGraphNode_Input", "AnimNode_LinkedInputPose", Link(asset, "InputPose", "PoseLink", -1)));
            Member(Kawaii, "AnimNode_KawaiiPhysics", Struct(asset, Kawaii, "AnimNode_KawaiiPhysics",
                Link(asset, "ComponentPose", "ComponentSpacePoseLink", 1), new FloatPropertyData(new FName(asset, "Stiffness")) { Value = 0.05f }));
            Member("AnimGraphNode_ToLocal", "AnimNode_ConvertComponentToLocalSpace", Struct(asset, "AnimGraphNode_ToLocal", "AnimNode_ConvertComponentToLocalSpace",
                Link(asset, "ComponentPose", "ComponentSpacePoseLink", 2)));

            var rows = Enumerable.Range(0, 4).Select(i => (PropertyData)Struct(asset, "AnimNodeData", "AnimNodeData",
                new IntPropertyData(new FName(asset, "NodeIndex")) { Value = i })).ToArray();
            var classExport = new ClassExport
            {
                Asset = asset,
                ObjectName = new FName(asset, "Graph_C"),
                LoadedProperties = [.. members],
                ClassDefaultObject = FPackageIndex.FromExport(0),
                OuterIndex = new FPackageIndex(0),
                SuperStruct = new FPackageIndex(0),
                Data = [new ArrayPropertyData(new FName(asset, "AnimNodeData")) { ArrayType = new FName(asset, "StructProperty"), Value = rows }],
            };
            asset.Exports.Add(classExport);
            return new AnimGraph(asset, classExport, cdo, 0);
        }

        public int LinkOf(string node) => ((IntPropertyData)((StructPropertyData)NodeValue(node).Value[0]).Value[0]).Value;

        public int[] NodeDataIndices() => Class.Data.OfType<ArrayPropertyData>().Single().Value
            .Select(r => ((IntPropertyData)((StructPropertyData)r).Value[0]).Value).ToArray();

        public string Snapshot() => string.Join(";",
            Class.LoadedProperties.Length,
            Cdo.Data.Count,
            string.Join(",", NodeDataIndices()),
            string.Join(",", Cdo.Data.Skip(1).Select(n => LinkOf(FNameDisplay.ToDisplayString(n.Name)))));

        private StructPropertyData NodeValue(string node) => (StructPropertyData)Cdo.Data.Single(p => FNameDisplay.ToDisplayString(p.Name) == node);
    }

    private static StructPropertyData Struct(UAsset asset, string name, string type, params PropertyData[] fields) =>
        new(new FName(asset, name)) { StructType = new FName(asset, type), Value = [.. fields] };

    private static StructPropertyData Link(UAsset asset, string name, string type, int linkId) =>
        Struct(asset, name, type, new IntPropertyData(new FName(asset, "LinkID")) { Value = linkId });
}
