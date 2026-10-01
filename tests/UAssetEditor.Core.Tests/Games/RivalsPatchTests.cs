using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.Games.Rivals;

namespace UAssetEditor.Core.Tests.Games;

public class RivalsPatchTests
{
    private static readonly KawaiiPhysicsPortOptions KawaiiOnly = new() { PatchKawaiiPhysics = true };

    /// <summary>A community-plugin node: settings directly on the node, including the plugin's "WaveFrequency" spelling.</summary>
    private static StructPropertyData LegacyNode(UAsset asset, string rootBone = "Spine_01") => new(new FName(asset, "Node"), new FName(asset, "AnimNode_KawaiiPhysics"))
    {
        Value =
        [
            new NamePropertyData(new FName(asset, "RootBone")) { Value = new FName(asset, rootBone) },
            new StructPropertyData(new FName(asset, "PhysicsSettings"), new FName(asset, "KawaiiPhysicsSettings"))
            {
                Value = [new FloatPropertyData(new FName(asset, "Damping")) { Value = 0.3f }],
            },
            new FloatPropertyData(new FName(asset, "DummyBoneLength")) { Value = 5f },
            new FloatPropertyData(new FName(asset, "WaveFrequency")) { Value = 2f },
        ],
    };

    private static NormalExport ExportWith(UAsset asset, params PropertyData[] properties)
    {
        // Ancestry resolution after a patch needs a class, so each export gets one.
        asset.Imports.Add(new Import("/Script/CoreUObject", "Package", new FPackageIndex(0), "/Script/Engine", false, asset));
        asset.Imports.Add(new Import("/Script/CoreUObject", "Class", FPackageIndex.FromImport(asset.Imports.Count - 1), "SkeletalMesh", false, asset));
        var export = new NormalExport(asset, [])
        {
            ObjectName = new FName(asset, "Export" + asset.Exports.Count),
            ClassIndex = FPackageIndex.FromImport(asset.Imports.Count - 1),
            Data = [.. properties],
        };
        asset.Exports.Add(export);
        return export;
    }

    private static PropertyData Child(PropertyData parent, string name) =>
        ((StructPropertyData)parent).Value.Single(p => p.Name.Value.Value == name);

    private static PropertyData[] Chains(StructPropertyData node) => ((ArrayPropertyData)Child(node, "Chains")).Value;

    [Fact]
    public void LegacyNode_IsRebuiltAsOneChainInTheGameLayout()
    {
        var asset = TestAssets.CreateAsset();
        var node = LegacyNode(asset);
        ExportWith(asset, node);

        var result = KawaiiPhysicsLegacyPorter.PortLegacyAnimNodes(asset, KawaiiOnly);

        Assert.Equal((1, 1, 0, 0), (result.VisitedAnimNodes, result.PortedAnimNodes, result.SkippedExistingChains, result.PatchedDefaultHiddenMaterialLods));
        Assert.True(result.Changed);
        var chain = (StructPropertyData)Assert.Single(Chains(node));
        Assert.Equal("KawaiiPhysicsChain", chain.StructType.Value.Value);

        var boneSettings = Child(chain, "BoneSettings");
        Assert.Equal("Spine_01", ((NamePropertyData)Child(boneSettings, "RootBone")).Value.Value.Value);
        Assert.Equal(5f, ((FloatPropertyData)Child(boneSettings, "DummyBoneLength")).Value);

        // The node's PhysicsSettings moves one level down, under the chain's BoneChainPhysicsSettings.
        var chainPhysics = (StructPropertyData)Child(chain, "PhysicsSettings");
        Assert.Equal("BoneChainPhysicsSettings", chainPhysics.StructType.Value.Value);
        var physics = (StructPropertyData)Child(chainPhysics, "PhysicsSettings");
        Assert.Equal("KawaiiPhysicsSettings", physics.StructType.Value.Value);
        Assert.Equal(0.3f, ((FloatPropertyData)Child(physics, "Damping")).Value);

        Assert.Equal(2f, ((FloatPropertyData)Child(Child(chain, "WaveAnimSettings"), "WaveFrequncy")).Value);
        Assert.Equal("ExternalForceSettings", ((StructPropertyData)Child(chain, "ExternalForceSettings")).StructType.Value.Value);
        Assert.Equal(-1, ((IntPropertyData)Child(chain, "AutoConfiguredLODThreshold")).Value);
    }

    [Fact]
    public void PortedChain_DoesNotShareStateWithTheNode()
    {
        var asset = TestAssets.CreateAsset();
        var node = LegacyNode(asset);
        ExportWith(asset, node);

        KawaiiPhysicsLegacyPorter.PortLegacyAnimNodes(asset, KawaiiOnly);
        ((FloatPropertyData)Child(Child(node, "PhysicsSettings"), "Damping")).Value = 0.9f;

        var chain = Chains(node)[0];
        Assert.Equal(0.3f, ((FloatPropertyData)Child(Child(Child(chain, "PhysicsSettings"), "PhysicsSettings"), "Damping")).Value);
    }

    [Fact]
    public void NodeWithChains_IsSkippedUnlessChainZeroIsForceRebuilt()
    {
        var asset = TestAssets.CreateAsset();
        var node = LegacyNode(asset, rootBone: "Hair_01");
        var existing0 = new StructPropertyData(new FName(asset, "Chains"), new FName(asset, "KawaiiPhysicsChain")) { Value = [] };
        var existing1 = new StructPropertyData(new FName(asset, "Chains"), new FName(asset, "KawaiiPhysicsChain")) { Value = [] };
        node.Value.Add(new ArrayPropertyData(new FName(asset, "Chains")) { ArrayType = new FName(asset, "StructProperty"), Value = [existing0, existing1] });
        ExportWith(asset, node);

        var skipped = KawaiiPhysicsLegacyPorter.PortLegacyAnimNodes(asset, KawaiiOnly);
        Assert.Equal((1, 0, 1), (skipped.VisitedAnimNodes, skipped.PortedAnimNodes, skipped.SkippedExistingChains));
        Assert.False(skipped.Changed);
        Assert.Same(existing0, Chains(node)[0]);

        var rebuilt = KawaiiPhysicsLegacyPorter.PortLegacyAnimNodes(asset, KawaiiOnly with { ForceRebuildChain0 = true });
        Assert.Equal((1, 1, 0), (rebuilt.VisitedAnimNodes, rebuilt.PortedAnimNodes, rebuilt.SkippedExistingChains));
        Assert.Equal(2, Chains(node).Length);
        Assert.Equal("Hair_01", ((NamePropertyData)Child(Child(Chains(node)[0], "BoneSettings"), "RootBone")).Value.Value.Value);
        Assert.Same(existing1, Chains(node)[1]);
    }

    [Fact]
    public void OtherStructsAndDisabledPort_LeaveTheAssetAlone()
    {
        var asset = TestAssets.CreateAsset();
        var other = new StructPropertyData(new FName(asset, "Node"), new FName(asset, "AnimNode_ModifyBone"))
        {
            Value = [new NamePropertyData(new FName(asset, "BoneToModify")) { Value = new FName(asset, "Spine_01") }],
        };
        var kawaii = LegacyNode(asset);
        ExportWith(asset, other, kawaii);

        var result = KawaiiPhysicsLegacyPorter.PortLegacyAnimNodes(asset, new KawaiiPhysicsPortOptions { PatchKawaiiPhysics = false });

        Assert.False(result.Changed);
        Assert.Equal(0, result.VisitedAnimNodes);
        Assert.Single(other.Value);
        Assert.DoesNotContain(kawaii.Value, p => p.Name.Value.Value == "Chains");
    }

    private static ArrayPropertyData LodInfo(UAsset asset, int lodCount) => new(new FName(asset, "LODInfo"))
    {
        ArrayType = new FName(asset, "StructProperty"),
        Value = [.. Enumerable.Range(0, lodCount).Select(_ => new StructPropertyData(new FName(asset, "LODInfo"), new FName(asset, "SkeletalMeshLODInfo"))
        {
            Value =
            [
                new FloatPropertyData(new FName(asset, "ScreenSize")) { Value = 1f },
                new ArrayPropertyData(new FName(asset, "LODMaterialMap")) { ArrayType = new FName(asset, "IntProperty"), Value = [] },
                new FloatPropertyData(new FName(asset, "LODHysteresis")) { Value = 0.02f },
            ],
        })],
    };

    private static bool[] HiddenFlags(PropertyData lod)
    {
        var lodStruct = (StructPropertyData)lod;
        Assert.Equal("DefaultHiddenMaterials", lodStruct.Value[2].Name.Value.Value); // right after LODMaterialMap
        return [.. ((ArrayPropertyData)lodStruct.Value[2]).Value.Cast<BoolPropertyData>().Select(b => b.Value)];
    }

    [Fact]
    public void Bitmaps_GiveEachLodItsOwnHiddenMaterials()
    {
        var asset = TestAssets.CreateAsset();
        var lodInfo = LodInfo(asset, 2);
        ExportWith(asset, lodInfo);

        var result = KawaiiPhysicsLegacyPorter.PortLegacyAnimNodes(asset, KawaiiOnly with { DefaultHiddenMaterialBitmaps = [0x5, 0x1] });

        Assert.Equal(2, result.PatchedDefaultHiddenMaterialLods);
        Assert.True(result.Changed);
        // No material count can be read from a fileless export, so each array is as long as its mask's highest set bit.
        Assert.Equal([true, false, true], HiddenFlags(lodInfo.Value[0]));
        Assert.Equal([true], HiddenFlags(lodInfo.Value[1]));
    }

    [Fact]
    public void SingleBitmap_AppliesToEveryLod()
    {
        var asset = TestAssets.CreateAsset();
        var lodInfo = LodInfo(asset, 3);
        ExportWith(asset, lodInfo);

        KawaiiPhysicsLegacyPorter.PortLegacyAnimNodes(asset, KawaiiOnly with { DefaultHiddenMaterialBitmaps = [0x2] });

        Assert.All(lodInfo.Value, lod => Assert.Equal([false, true], HiddenFlags(lod)));
    }

    [Fact]
    public void MoreBitmapsThanLods_IsRefused()
    {
        var asset = TestAssets.CreateAsset();
        ExportWith(asset, LodInfo(asset, 1));

        var ex = Assert.Throws<InvalidDataException>(() =>
            KawaiiPhysicsLegacyPorter.PortLegacyAnimNodes(asset, KawaiiOnly with { DefaultHiddenMaterialBitmaps = [0x1, 0x2] }));
        Assert.Contains("only 1 LOD", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BitmapList_ParsesDecimalAndHexWithAnySeparator()
    {
        Assert.Equal([5UL, 3UL, 16UL, ulong.MaxValue], DefaultHiddenMaterialBitmaps.Parse(" 0x5, 3|0X10;18446744073709551615 "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData(",;|")]
    [InlineData("0xZZ")]
    [InlineData("-1")]
    [InlineData("18446744073709551616")]
    [InlineData("0x")]
    public void BitmapList_RejectsAnythingElse(string? text)
    {
        Assert.Throws<FormatException>(() => DefaultHiddenMaterialBitmaps.Parse(text));
    }

    [Fact]
    public void PatchDirectory_NeverOpensPackagesWithoutKawaiiPhysics()
    {
        var dir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Rivals_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            // Not a package at all: opening it would throw, so getting an empty result proves it was skipped.
            var path = Path.Combine(dir, "NotKawaii.uasset");
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("plain bytes"));

            var patched = RivalsAssetPatcher.PatchDirectory(dir, Path.Combine(dir, "missing.usmap"), KawaiiOnly);

            Assert.Empty(patched);
            Assert.Equal("plain bytes", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PatchDirectory_NamesTheAssetItCannotOpen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Rivals_" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(dir, "Sub"));
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Sub", "Broken.uasset"), Encoding.ASCII.GetBytes("mentions KawaiiPhysics but is no package"));

            var ex = Assert.Throws<InvalidDataException>(() =>
                RivalsAssetPatcher.PatchDirectory(dir, Path.Combine(dir, "missing.usmap"), KawaiiOnly));

            Assert.StartsWith(Path.Combine("Sub", "Broken.uasset") + ":", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
