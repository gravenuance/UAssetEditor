using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests;

public class StructFieldEditorTests
{
    [Fact]
    public void AppendClone_AddsADeepCloneOfAnUnrelatedPropertyIntoAnEmptyStruct()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        TestAssets.AddPurelyStructuralStruct(asset, export);
        var emptyInner = (UAssetAPI.PropertyTypes.Structs.StructPropertyData)
            PropertyLocator.Locate(asset, 0, "MiddleContainer.EmptyInner")!.Property;
        var locationX = PropertyLocator.Locate(asset, 0, "Location.X")!.Property;

        var clone = StructFieldEditor.AppendClone(emptyInner, locationX);

        Assert.Single(emptyInner.Value);
        Assert.Same(clone, emptyInner.Value[0]);
        Assert.Equal("1.5", PropertyValueAccessor.AsSearchableString(
            PropertyLocator.Locate(asset, 0, "MiddleContainer.EmptyInner.X")!.Property, asset));
    }

    [Fact]
    public void AppendClone_ProducesAnIndependentCopy_NotASharedReference()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        TestAssets.AddPurelyStructuralStruct(asset, export);
        var emptyInner = (UAssetAPI.PropertyTypes.Structs.StructPropertyData)
            PropertyLocator.Locate(asset, 0, "MiddleContainer.EmptyInner")!.Property;
        var locationX = PropertyLocator.Locate(asset, 0, "Location.X")!.Property;

        StructFieldEditor.AppendClone(emptyInner, locationX);

        var clonedX = PropertyLocator.Locate(asset, 0, "MiddleContainer.EmptyInner.X")!.Property;
        Assert.True(PropertyValueAccessor.TrySetStringValue(clonedX, "999", asset));

        Assert.Equal("1.5", PropertyValueAccessor.AsSearchableString(
            PropertyLocator.Locate(asset, 0, "Location.X")!.Property, asset));
    }

    [Fact]
    public void AppendClone_OnExportRoot_AddsAWholeMissingTopLevelProperty()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        // "Location" (a whole struct) isn't present at all on a second export - only its own scalars are.
        var otherExport = TestAssets.CreateSampleExport(asset, "OtherExport");
        otherExport.Data.RemoveAll(p => p.Name.Value?.Value == "Location");
        Assert.Null(PropertyLocator.Locate(asset, 1, "Location"));
        var location = PropertyLocator.Locate(asset, 0, "Location")!.Property;

        var rootData = PropertyLocator.LocateExportData(asset, 1)!;
        var clone = StructFieldEditor.AppendClone(rootData, location);

        Assert.Same(clone, PropertyLocator.Locate(asset, 1, "Location")!.Property);
        Assert.Equal("1.5", PropertyValueAccessor.AsSearchableString(
            PropertyLocator.Locate(asset, 1, "Location.X")!.Property, asset));
    }
}
