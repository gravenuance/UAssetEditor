using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.Tests;

/// <summary>Cloning in the vendored UAssetAPI must copy every collection a property owns, not share or mix them up.</summary>
public class PropertyCloneTests
{
    [Fact]
    public void SetClone_CopiesElementsToRemoveFromItself_NotFromValue()
    {
        var asset = TestAssets.CreateAsset();
        var set = SampleSet(asset, values: [1], removed: [7]);

        var clone = (SetPropertyData)set.Clone();

        Assert.Equal(7, ((IntPropertyData)clone.ElementsToRemove.Single()).Value);
    }

    [Fact]
    public void SetClone_WithMoreValuesThanRemovals_DoesNotThrow()
    {
        var asset = TestAssets.CreateAsset();
        var set = SampleSet(asset, values: [1, 2, 3], removed: [7]);

        var clone = (SetPropertyData)set.Clone();

        Assert.Equal([1, 2, 3], clone.Value.Select(v => ((IntPropertyData)v).Value));
        Assert.Single(clone.ElementsToRemove);
    }

    [Fact]
    public void SetClone_ElementsToRemoveAreIndependentCopies()
    {
        var asset = TestAssets.CreateAsset();
        var set = SampleSet(asset, values: [1], removed: [7]);

        var clone = (SetPropertyData)set.Clone();
        ((IntPropertyData)clone.ElementsToRemove[0]).Value = 99;

        Assert.Equal(7, ((IntPropertyData)set.ElementsToRemove[0]).Value);
    }

    private static SetPropertyData SampleSet(UAssetAPI.UAsset asset, int[] values, int[] removed) => new(new FName(asset, "Tags"))
    {
        ArrayType = new FName(asset, "IntProperty"),
        Value = [.. values.Select(v => (PropertyData)new IntPropertyData(new FName(asset, "Tags")) { Value = v })],
        ElementsToRemove = [.. removed.Select(v => (PropertyData)new IntPropertyData(new FName(asset, "Tags")) { Value = v })],
    };
}
