using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests;

public class CrossAssetCopierTests
{
    [Fact]
    public void Copy_RecreatesEveryNameInTheTargetAndLeavesTheDonorAlone()
    {
        var donor = TestAssets.CreateAsset();
        var target = TestAssets.CreateAsset();
        var value = new StructPropertyData(new FName(donor, "Chain"))
        {
            StructType = new FName(donor, "KawaiiPhysicsChain"),
            Value = [new NamePropertyData(new FName(donor, "BoneName")) { Value = new FName(donor, "breast_l") }],
        };

        var copy = (StructPropertyData)new CrossAssetCopier(donor, target).Copy(value);

        var bone = ((NamePropertyData)copy.Value[0]).Value;
        Assert.Same(target, bone.Asset);
        Assert.Equal("breast_l", bone.Value.Value);
        Assert.True(target.ContainsNameReference(new FString("breast_l")));
        Assert.Same(target, copy.StructType.Asset);
        Assert.Same(donor, ((NamePropertyData)value.Value[0]).Value.Asset);
    }

    [Fact]
    public void Copy_BringsReferencedImportsAlongOnceWithTheirOuterPackage()
    {
        var donor = TestAssets.CreateAsset();
        var target = TestAssets.CreateAsset();
        donor.Imports.Add(new Import("/Script/CoreUObject", "Package", new FPackageIndex(0), "/Script/KawaiiPhysics", false, donor));
        donor.Imports.Add(new Import("/Script/CoreUObject", "ScriptStruct", FPackageIndex.FromImport(0), "AnimNode_KawaiiPhysics", false, donor));
        var reference = new ObjectPropertyData(new FName(donor, "Struct")) { Value = FPackageIndex.FromImport(1) };
        var copier = new CrossAssetCopier(donor, target);

        var first = (ObjectPropertyData)copier.Copy(reference);
        var second = (ObjectPropertyData)copier.Copy(reference);

        Assert.Equal(2, target.Imports.Count);
        Assert.Equal(first.Value.Index, second.Value.Index);
        var copied = first.Value.ToImport(target);
        Assert.Equal("AnimNode_KawaiiPhysics", copied.ObjectName.Value.Value);
        Assert.Equal("/Script/KawaiiPhysics", copied.OuterIndex.ToImport(target).ObjectName.Value.Value);
    }

    [Fact]
    public void Copy_MapsDonorExportsAndRefusesUnmappedOnes()
    {
        var donor = TestAssets.CreateAsset();
        var target = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(donor, "DonorClass");
        TestAssets.CreateSampleExport(target, "TargetClass");
        var reference = new ObjectPropertyData(new FName(donor, "AnimClassInterface")) { Value = FPackageIndex.FromExport(0) };
        var copier = new CrossAssetCopier(donor, target);

        Assert.Throws<InvalidOperationException>(() => copier.Copy(reference));

        copier.MapExport(0, 0);
        Assert.Equal(FPackageIndex.FromExport(0).Index, ((ObjectPropertyData)copier.Copy(reference)).Value.Index);
    }
}
