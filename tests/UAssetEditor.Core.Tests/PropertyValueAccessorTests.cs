using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests;

public class PropertyValueAccessorTests
{
    [Fact]
    public void AsSearchableString_ReadsScalarAndStringKinds()
    {
        var asset = TestAssets.CreateAsset();
        var export = TestAssets.CreateSampleExport(asset);
        var byPath = PropertyWalker.Walk(export).ToDictionary(n => n.Path, n => n.Property);

        Assert.Equal("True", PropertyValueAccessor.AsSearchableString(byPath["bEnabled"], asset));
        Assert.Equal("5", PropertyValueAccessor.AsSearchableString(byPath["Count"], asset));
        Assert.Equal("Hello World", PropertyValueAccessor.AsSearchableString(byPath["DisplayName"], asset));
        Assert.Equal("Alpha", PropertyValueAccessor.AsSearchableString(byPath["Tags[0]"], asset));
    }

    [Fact]
    public void TrySetStringValue_RoundTripsThroughAsSearchableString()
    {
        var asset = TestAssets.CreateAsset();
        var prop = new IntPropertyData(new FName(asset, "Count")) { Value = 5 };

        var ok = PropertyValueAccessor.TrySetStringValue(prop, "42", asset);

        Assert.True(ok);
        Assert.Equal(42, prop.Value);
        Assert.Equal("42", PropertyValueAccessor.AsSearchableString(prop, asset));
    }

    [Fact]
    public void TrySetStringValue_RejectsUnparsableValueForTypedProperty()
    {
        var asset = TestAssets.CreateAsset();
        var prop = new IntPropertyData(new FName(asset, "Count")) { Value = 5 };

        var ok = PropertyValueAccessor.TrySetStringValue(prop, "not-a-number", asset);

        Assert.False(ok);
        Assert.Equal(5, prop.Value);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, false)]
    public void UpdateIsZeroFlag_ReflectsCurrentIntValue(int value, bool expectedIsZero)
    {
        var asset = TestAssets.CreateAsset();
        var prop = new IntPropertyData(new FName(asset, "Count")) { Value = value, IsZero = !expectedIsZero };

        PropertyValueAccessor.UpdateIsZeroFlag(prop);

        Assert.Equal(expectedIsZero, prop.IsZero);
    }

    [Fact]
    public void UpdateIsZeroFlag_ReflectsEmptyString()
    {
        var asset = TestAssets.CreateAsset();
        var prop = new StrPropertyData(new FName(asset, "DisplayName")) { Value = new FString(""), IsZero = false };

        PropertyValueAccessor.UpdateIsZeroFlag(prop);

        Assert.True(prop.IsZero);
    }
}
