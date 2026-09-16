using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests;

public class ArrayElementEditorTests
{
    [Fact]
    public void Duplicate_AppendsADeepCloneAsTheLastElement()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        var chains = TestAssets.AddStructArray(asset, export, "Chains", 10, 20);

        var clone = ArrayElementEditor.Duplicate(chains, 0);

        Assert.Equal(3, chains.Value.Length);
        Assert.Same(clone, chains.Value[2]);
        Assert.Equal("10", PropertyValueAccessor.AsSearchableString(
            PropertyLocator.Locate(asset, 0, "Chains[2].Value")!.Property, asset));
    }

    [Fact]
    public void Duplicate_ProducesAnIndependentCopy_NotASharedReference()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        var chains = TestAssets.AddStructArray(asset, export, "Chains", 10, 20);

        ArrayElementEditor.Duplicate(chains, 0);

        // Editing the clone must not also change the original element it was cloned from.
        var clonedValue = PropertyLocator.Locate(asset, 0, "Chains[2].Value")!.Property;
        Assert.True(PropertyValueAccessor.TrySetStringValue(clonedValue, "999", asset));

        Assert.Equal("10", PropertyValueAccessor.AsSearchableString(
            PropertyLocator.Locate(asset, 0, "Chains[0].Value")!.Property, asset));
    }

    [Fact]
    public void Duplicate_ThrowsForAnOutOfRangeIndex()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        var chains = TestAssets.AddStructArray(asset, export, "Chains", 10);

        Assert.Throws<ArgumentOutOfRangeException>(() => ArrayElementEditor.Duplicate(chains, 5));
    }

    [Fact]
    public void AppendClone_AppendsADeepCloneOfAnUnrelatedPropertyIntoAnEmptyArray()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        var excludeBones = TestAssets.AddStructArray(asset, export, "ExcludeBones");
        var location = PropertyLocator.Locate(asset, 0, "Location")!.Property;

        var clone = ArrayElementEditor.AppendClone(excludeBones, location);

        Assert.Single(excludeBones.Value);
        Assert.Same(clone, excludeBones.Value[0]);
        Assert.Equal("1.5", PropertyValueAccessor.AsSearchableString(
            PropertyLocator.Locate(asset, 0, "ExcludeBones[0].X")!.Property, asset));
    }

    [Fact]
    public void AppendClone_ProducesAnIndependentCopy_NotASharedReference()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        var excludeBones = TestAssets.AddStructArray(asset, export, "ExcludeBones");
        var location = PropertyLocator.Locate(asset, 0, "Location")!.Property;

        ArrayElementEditor.AppendClone(excludeBones, location);

        var clonedX = PropertyLocator.Locate(asset, 0, "ExcludeBones[0].X")!.Property;
        Assert.True(PropertyValueAccessor.TrySetStringValue(clonedX, "999", asset));

        Assert.Equal("1.5", PropertyValueAccessor.AsSearchableString(
            PropertyLocator.Locate(asset, 0, "Location.X")!.Property, asset));
    }

    [Fact]
    public void RemoveAt_RemovesTheElementAndShiftsLaterOnesDown()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        var chains = TestAssets.AddStructArray(asset, export, "Chains", 10, 20, 30);

        ArrayElementEditor.RemoveAt(chains, 1);

        Assert.Equal(2, chains.Value.Length);
        Assert.Equal("10", PropertyValueAccessor.AsSearchableString(
            PropertyLocator.Locate(asset, 0, "Chains[0].Value")!.Property, asset));
        Assert.Equal("30", PropertyValueAccessor.AsSearchableString(
            PropertyLocator.Locate(asset, 0, "Chains[1].Value")!.Property, asset));
    }

    [Fact]
    public void RemoveAt_ThrowsForAnOutOfRangeIndex()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        var chains = TestAssets.AddStructArray(asset, export, "Chains", 10);

        Assert.Throws<ArgumentOutOfRangeException>(() => ArrayElementEditor.RemoveAt(chains, -1));
    }
}
