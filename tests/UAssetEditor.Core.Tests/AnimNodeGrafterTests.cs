using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.FieldTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests;

public class AnimNodeGrafterTests
{
    private const string Kawaii = "AnimGraphNode_KawaiiPhysics_21";

    [Fact]
    public void GraftBeforeRoot_ChainsTheCopiesBetweenRootAndItsOldInput()
    {
        var donor = Blueprint.Donor();
        var target = Blueprint.Target();

        var grafted = AnimNodeGrafter.GraftBeforeRoot(target.Asset, target.CdoIndex, donor.Asset, donor.CdoIndex,
            ["AnimGraphNode_LocalToComponentSpace", Kawaii]);

        Assert.Equal(
            [new GraftedAnimNode("AnimGraphNode_LocalToComponentSpace", "AnimGraphNode_LocalToComponentSpace_1", 2), new GraftedAnimNode(Kawaii, "AnimGraphNode_KawaiiPhysics_1", 3)],
            grafted);
        Assert.Equal(3, target.LinkOf("AnimGraphNode_Root"));
        Assert.Equal(2, target.LinkOf("AnimGraphNode_KawaiiPhysics_1"));
        Assert.Equal(1, target.LinkOf("AnimGraphNode_LocalToComponentSpace_1"));
        Assert.Equal([0, 1, 2, 3], target.RowIndices());
        Assert.All(target.RowInterfaces(), i => Assert.Equal(FPackageIndex.FromExport(target.ClassIndex).Index, i));
    }

    [Fact]
    public void GraftBeforeRoot_GivesEveryCopyAnExposedValueHandlerThatDoesNothing()
    {
        var donor = Blueprint.Donor();
        var target = Blueprint.Target();
        TestAssets.AddSparseHandlers(target.Asset, target.Cdo, "None", "EvaluateInputPose");

        AnimNodeGrafter.GraftBeforeRoot(target.Asset, target.CdoIndex, donor.Asset, donor.CdoIndex, ["AnimGraphNode_LocalToComponentSpace", Kawaii]);

        Assert.Equal(["None", "EvaluateInputPose", "None", "None"], TestAssets.SparseHandlers(target.Asset, target.Cdo));
    }

    [Fact]
    public void GraftBeforeRoot_RecreatesNamesAndBringsTheNodeTypeAndImportAlong()
    {
        var donor = Blueprint.Donor();
        var target = Blueprint.Target();

        AnimNodeGrafter.GraftBeforeRoot(target.Asset, target.CdoIndex, donor.Asset, donor.CdoIndex, [Kawaii]);

        var node = target.Node("AnimGraphNode_KawaiiPhysics_1");
        var bone = node.Value.OfType<NamePropertyData>().Single().Value;
        Assert.Same(target.Asset, bone.Asset);
        Assert.Equal("breast_l", bone.Value.Value);
        Assert.Contains("/Script/KawaiiPhysics.AnimNode_KawaiiPhysics", target.NodeTypes());
        Assert.Contains(target.Asset.Imports, i => i.ObjectName.Value.Value == "AnimNode_KawaiiPhysics");
    }

    [Fact]
    public void GraftBeforeRoot_PointsCopiedEntriesAtTheSameConstantWhereverTheTargetKeepsIt()
    {
        var donor = Blueprint.Donor();
        var target = Blueprint.Target(constants: ["StructProperty:__StructProperty", "NameProperty:__NameProperty", "NameProperty:__NameProperty"]);

        AnimNodeGrafter.GraftBeforeRoot(target.Asset, target.CdoIndex, donor.Asset, donor.CdoIndex, [Kawaii]);

        Assert.Equal([0u, uint.MaxValue], target.EntriesOfRow(2));
    }

    [Fact]
    public void GraftBeforeRoot_TellsStructConstantsApartByTheirStructType()
    {
        var donor = Blueprint.Donor();
        var target = Blueprint.Target(constants: ["StructProperty:__StructProperty:FloatRange", "NameProperty:__NameProperty", "StructProperty:__StructProperty"]);

        AnimNodeGrafter.GraftBeforeRoot(target.Asset, target.CdoIndex, donor.Asset, donor.CdoIndex, [Kawaii]);

        Assert.Equal([2u, uint.MaxValue], target.EntriesOfRow(2));
    }

    [Fact]
    public void GraftBeforeRoot_RefusesWhenTheTargetLacksTheConstant_AndChangesNothing()
    {
        var donor = Blueprint.Donor();
        var target = Blueprint.Target(constants: ["NameProperty:__NameProperty", "NameProperty:__NameProperty", "NameProperty:__NameProperty"]);
        var before = target.Snapshot();

        Assert.Throws<InvalidOperationException>(() =>
            AnimNodeGrafter.GraftBeforeRoot(target.Asset, target.CdoIndex, donor.Asset, donor.CdoIndex, [Kawaii]));

        Assert.Equal(before, target.Snapshot());
    }

    [Fact]
    public void GraftBeforeRoot_RefusesAnUnknownDonorNode_AndChangesNothing()
    {
        var donor = Blueprint.Donor();
        var target = Blueprint.Target();
        var before = target.Snapshot();

        Assert.Throws<InvalidOperationException>(() =>
            AnimNodeGrafter.GraftBeforeRoot(target.Asset, target.CdoIndex, donor.Asset, donor.CdoIndex, ["AnimGraphNode_Nope"]));

        Assert.Equal(before, target.Snapshot());
    }

    private sealed class Blueprint
    {
        private readonly List<FProperty> _members = [];
        private readonly List<PropertyData> _rows = [];
        private readonly MapPropertyData _types;

        private static readonly string[] DefaultConstants = ["NameProperty:__NameProperty", "NameProperty:__NameProperty", "StructProperty:__StructProperty"];

        private Blueprint(string className, string[] constants)
        {
            Asset = TestAssets.CreateAsset();
            Cdo = new NormalExport(Asset, []) { ObjectName = new FName(Asset, $"Default__{className}"), Data = [] };
            Asset.Exports.Add(Cdo);
            Asset.Exports.Add(new StructExport
            {
                Asset = Asset,
                ObjectName = new FName(Asset, "AnimBlueprintGeneratedConstantData"),
                LoadedProperties = constants.Select((kind, i) => Constant(kind.Split(':'), i)).ToArray(),
                OuterIndex = new FPackageIndex(0),
                SuperStruct = new FPackageIndex(0),
            });
            _types = new MapPropertyData(new FName(Asset, "NodeTypeMap"))
            {
                KeyType = new FName(Asset, "ObjectProperty"),
                ValueType = new FName(Asset, "StructProperty"),
                Value = new TMap<PropertyData, PropertyData>(),
            };
            Class = new ClassExport
            {
                Asset = Asset,
                ObjectName = new FName(Asset, className),
                ClassDefaultObject = FPackageIndex.FromExport(0),
                OuterIndex = new FPackageIndex(0),
                SuperStruct = new FPackageIndex(0),
            };
            Asset.Exports.Add(Class);
        }

        public UAsset Asset { get; }
        public ClassExport Class { get; }
        public NormalExport Cdo { get; }
        public int CdoIndex { get; }
        public int ClassIndex { get; } = 2;

        public static Blueprint Donor()
        {
            var b = new Blueprint("Post_Physics_C", DefaultConstants);
            b.Add("AnimGraphNode_Root", "/Script/Engine", "AnimNode_Root", "Result", "PoseLink", 2);
            b.Add("AnimGraphNode_LocalToComponentSpace", "/Script/Engine", "AnimNode_ConvertLocalToComponentSpace", "LocalPose", "PoseLink", -1);
            b.Add(Kawaii, "/Script/KawaiiPhysics", "AnimNode_KawaiiPhysics", "ComponentPose", "ComponentSpacePoseLink", 1,
                new NamePropertyData(new FName(b.Asset, "RootBone")) { Value = new FName(b.Asset, "breast_l") });
            return b.Seal();
        }

        public static Blueprint Target(string[]? constants = null)
        {
            var b = new Blueprint("Post_Lobby_Physics_C", constants ?? DefaultConstants);
            b.Add("AnimGraphNode_Root", "/Script/Engine", "AnimNode_Root", "Result", "PoseLink", 1);
            b.Add("AnimGraphNode_LinkedInputPose", "/Script/Engine", "AnimNode_LinkedInputPose", "InputPose", "PoseLink", -1);
            return b.Seal();
        }

        public StructPropertyData Node(string name) => (StructPropertyData)Cdo.Data.Single(p => FNameDisplay.ToDisplayString(p.Name) == name);

        public int LinkOf(string node) => ((IntPropertyData)Node(node).Value.OfType<StructPropertyData>().First().Value[0]).Value;

        public int[] RowIndices() => Rows().Select(r => r.Value.OfType<IntPropertyData>().Single().Value).ToArray();

        public uint[] EntriesOfRow(int row) => Rows().ElementAt(row).Value.OfType<ArrayPropertyData>().Single().Value.Cast<UInt32PropertyData>().Select(e => e.Value).ToArray();

        public int[] RowInterfaces() => Rows().Select(r => r.Value.OfType<ObjectPropertyData>().Single().Value.Index).ToArray();

        public List<string> NodeTypes() => _types.Value.Keys.Select(k => ImportPathResolver.GetFullPath(((ObjectPropertyData)k).Value.ToImport(Asset), Asset)).ToList();

        public string Snapshot() => string.Join(";", Class.LoadedProperties.Length, Cdo.Data.Count, string.Join(",", RowIndices()), _types.Value.Count, Asset.Imports.Count, LinkOf("AnimGraphNode_Root"));

        private IEnumerable<StructPropertyData> Rows() => Class.Data.OfType<ArrayPropertyData>().Single().Value.Cast<StructPropertyData>();

        private void Add(string name, string package, string structType, string linkName, string linkType, int linkId, params PropertyData[] extra)
        {
            var structImport = Import(package, structType);
            _members.Add(new FStructProperty { Name = new FName(Asset, name), SerializedType = new FName(Asset, "StructProperty"), Struct = structImport });

            var link = new StructPropertyData(new FName(Asset, linkName)) { StructType = new FName(Asset, linkType), Value = [new IntPropertyData(new FName(Asset, "LinkID")) { Value = linkId }] };
            Cdo.Data.Add(new StructPropertyData(new FName(Asset, name)) { StructType = new FName(Asset, structType), Value = [link, .. extra] });

            _rows.Add(new StructPropertyData(new FName(Asset, "AnimNodeData"))
            {
                StructType = new FName(Asset, "AnimNodeData"),
                Value =
                [
                    new InterfacePropertyData(new FName(Asset, "AnimClassInterface")) { Value = FPackageIndex.FromExport(ClassIndex) },
                    new ArrayPropertyData(new FName(Asset, "Entries")) { ArrayType = new FName(Asset, "UInt32Property"), Value = [new UInt32PropertyData(new FName(Asset, "Entries")) { Value = 2 }, new UInt32PropertyData(new FName(Asset, "Entries")) { Value = uint.MaxValue }] },
                    new IntPropertyData(new FName(Asset, "NodeIndex")) { Value = _rows.Count },
                ],
            });

            if (!_types.Value.Keys.Any(k => ((ObjectPropertyData)k).Value.Index == structImport.Index))
                _types.Value.Add(new ObjectPropertyData(new FName(Asset, "NodeTypeMap")) { Value = structImport }, new StructPropertyData(new FName(Asset, "NodeTypeMap")) { StructType = new FName(Asset, "AnimNodeStructData"), Value = [] });
        }

        // "Type:Name[:Struct]" - struct constants name the struct they hold; the default is the shared AnimNodeFunctionRef.
        private FProperty Constant(string[] kind, int i) => kind[0] == "StructProperty"
            ? new FStructProperty { Name = new FName(Asset, kind[1], i + 10), SerializedType = new FName(Asset, kind[0]), Struct = Import("/Script/Engine", kind.Length > 2 ? kind[2] : "AnimNodeFunctionRef") }
            : new FGenericProperty { Name = new FName(Asset, kind[1], i + 10), SerializedType = new FName(Asset, kind[0]) };

        private FPackageIndex Import(string package, string objectName)
        {
            var packageIndex = Asset.Imports.FindIndex(i => i.ObjectName.Value.Value == package);
            if (packageIndex < 0)
            {
                Asset.Imports.Add(new UAssetAPI.Import("/Script/CoreUObject", "Package", new FPackageIndex(0), package, false, Asset));
                packageIndex = Asset.Imports.Count - 1;
            }
            Asset.Imports.Add(new UAssetAPI.Import("/Script/CoreUObject", "ScriptStruct", FPackageIndex.FromImport(packageIndex), objectName, false, Asset));
            return FPackageIndex.FromImport(Asset.Imports.Count - 1);
        }

        private Blueprint Seal()
        {
            Class.LoadedProperties = [.. _members];
            Class.Data = [new ArrayPropertyData(new FName(Asset, "AnimNodeData")) { ArrayType = new FName(Asset, "StructProperty"), Value = [.. _rows] }, _types];
            return this;
        }
    }
}
