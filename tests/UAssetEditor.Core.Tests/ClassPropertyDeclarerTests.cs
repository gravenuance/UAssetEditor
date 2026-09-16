using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests;

public class ClassPropertyDeclarerTests
{
    [Fact]
    public void DeclareClonedProperty_AddsANewDeclarationAndDefaultValue()
    {
        var asset = TestAssets.CreateAsset();
        var (classExport, cdo) = TestAssets.CreateClassWithNode(asset, "AnimGraphNode_KawaiiPhysics_1");
        var cdoIndex = asset.Exports.IndexOf(cdo);

        var (newName, value) = ClassPropertyDeclarer.DeclareClonedProperty(asset, cdoIndex, "AnimGraphNode_KawaiiPhysics_1");

        Assert.Equal("AnimGraphNode_KawaiiPhysics_2", newName);
        Assert.Equal(2, classExport.LoadedProperties.Length);
        // A real compiled class declares "Foo_N" siblings as FName(String="Foo", Number=N+1),
        // not as one literal "Foo_N" string with Number 0 (confirmed against a real asset - see
        // ClassPropertyDeclarer.DisplayName's own doc comment) - the new LoadedProperties entry
        // must follow that same convention, not just look right when read back through this tool.
        Assert.Contains(classExport.LoadedProperties, p => p.Name.Value?.Value == "AnimGraphNode_KawaiiPhysics" && p.Name.Number == 3);
        Assert.Contains(cdo.Data, p => p.Name.Value?.Value == newName);
        Assert.Same(cdo.Data.Last(), value);
    }

    [Fact]
    public void DeclareClonedProperty_ClonesTheTemplateValueRatherThanSharingIt()
    {
        var asset = TestAssets.CreateAsset();
        var (_, cdo) = TestAssets.CreateClassWithNode(asset, "AnimGraphNode_KawaiiPhysics_1");
        var cdoIndex = asset.Exports.IndexOf(cdo);

        var (_, cloned) = ClassPropertyDeclarer.DeclareClonedProperty(asset, cdoIndex, "AnimGraphNode_KawaiiPhysics_1");

        var template = (StructPropertyData)cdo.Data[0];
        var clonedStruct = (StructPropertyData)cloned;
        ((IntPropertyData)clonedStruct.Value[0]).Value = 99;

        // Mutating the clone's own field must not affect the template's - a deep clone, not a
        // shared reference to the same nested PropertyData objects.
        Assert.Equal(1, ((IntPropertyData)template.Value[0]).Value);
    }

    [Fact]
    public void DeclareClonedProperty_FollowsTheTemplatesOwnNumberingConvention()
    {
        var asset = TestAssets.CreateAsset();
        var (_, cdo) = TestAssets.CreateClassWithNode(asset, "AnimGraphNode_KawaiiPhysics_1");
        var cdoIndex = asset.Exports.IndexOf(cdo);

        var (first, _) = ClassPropertyDeclarer.DeclareClonedProperty(asset, cdoIndex, "AnimGraphNode_KawaiiPhysics_1");
        var (second, _) = ClassPropertyDeclarer.DeclareClonedProperty(asset, cdoIndex, "AnimGraphNode_KawaiiPhysics_1");

        Assert.Equal("AnimGraphNode_KawaiiPhysics_2", first);
        Assert.Equal("AnimGraphNode_KawaiiPhysics_3", second);
    }

    [Fact]
    public void DeclareClonedProperty_FindsATemplateDeclaredWithRealFNameNumberEncoding()
    {
        // Regression test: a real compiled class declares "AnimGraphNode_KawaiiPhysics_34" as
        // FName(String="AnimGraphNode_KawaiiPhysics", Number=35), not as one literal string with
        // Number 0 - the original implementation only ever compared raw FName.Value strings, so
        // it could never find a numbered template against a real asset (confirmed live: every
        // lookup for "..._N" failed with "isn't a declared struct property on this class").
        var asset = TestAssets.CreateAsset();
        var (classExport, cdo) = TestAssets.CreateClassWithNumberedNode(asset, "AnimGraphNode_KawaiiPhysics", displayNumber: 34);
        var cdoIndex = asset.Exports.IndexOf(cdo);

        // The lone existing sibling is "_34" - "first available starting from 1" (this method's
        // own established convention, unrelated to what's being regression-tested here) is "_1",
        // since nothing named "_1" exists yet in this single-node fixture.
        var (newName, _) = ClassPropertyDeclarer.DeclareClonedProperty(asset, cdoIndex, "AnimGraphNode_KawaiiPhysics_34");

        Assert.Equal("AnimGraphNode_KawaiiPhysics_1", newName);
        Assert.Contains(classExport.LoadedProperties, p => p.Name.Value?.Value == "AnimGraphNode_KawaiiPhysics" && p.Name.Number == 2);
    }

    [Fact]
    public void DeclareClonedProperty_ThrowsForAnExportThatIsNotACdoOfAnyClass()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        var exportIndex = asset.Exports.IndexOf(export);

        Assert.Throws<InvalidOperationException>(() => ClassPropertyDeclarer.DeclareClonedProperty(asset, exportIndex, "Location"));
    }

    [Fact]
    public void DeclareClonedProperty_ThrowsForAnUnknownTemplateName()
    {
        var asset = TestAssets.CreateAsset();
        var (_, cdo) = TestAssets.CreateClassWithNode(asset, "AnimGraphNode_KawaiiPhysics_1");
        var cdoIndex = asset.Exports.IndexOf(cdo);

        Assert.Throws<InvalidOperationException>(() => ClassPropertyDeclarer.DeclareClonedProperty(asset, cdoIndex, "NoSuchProperty"));
    }
}
