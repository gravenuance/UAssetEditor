using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
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
    public void AsSearchableString_ReadsEnumValueByName()
    {
        var asset = TestAssets.CreateAsset();
        var prop = new EnumPropertyData(new FName(asset, "BoneConstraintGlobalComplianceType"))
        {
            EnumType = new FName(asset, "EBoneConstraintGlobalComplianceType"),
            Value = new FName(asset, "Leather"),
        };

        Assert.Equal("Leather", PropertyValueAccessor.AsSearchableString(prop, asset));
    }

    [Fact]
    public void TrySetStringValue_SetsEnumValueByName()
    {
        var asset = TestAssets.CreateAsset();
        var prop = new EnumPropertyData(new FName(asset, "BoneConstraintGlobalComplianceType"))
        {
            EnumType = new FName(asset, "EBoneConstraintGlobalComplianceType"),
            Value = new FName(asset, "Leather"),
        };

        var ok = PropertyValueAccessor.TrySetStringValue(prop, "Fat", asset);

        Assert.True(ok);
        Assert.Equal("Fat", PropertyValueAccessor.AsSearchableString(prop, asset));
    }

    [Fact]
    public void TrySetStringValue_SetsObjectPropertyDataToMatchingImport()
    {
        var asset = TestAssets.CreateAsset();
        asset.Imports.Add(new Import("/Script/Engine", "Package", FPackageIndex.FromRawIndex(0), "/Game/Textures/T_Wall", false, asset));
        asset.Imports.Add(new Import("/Script/Engine", "Texture2D", FPackageIndex.FromImport(0), "T_Wall", false, asset));
        var prop = new ObjectPropertyData(new FName(asset, "BaseColor")) { Value = FPackageIndex.FromRawIndex(0) };

        var ok = PropertyValueAccessor.TrySetStringValue(prop, "/Game/Textures/T_Wall.T_Wall", asset);

        Assert.True(ok);
        Assert.Equal("/Game/Textures/T_Wall.T_Wall", PropertyValueAccessor.AsSearchableString(prop, asset));
    }

    [Fact]
    public void TrySetStringValue_RejectsObjectPropertyDataWithNoMatchingImport()
    {
        var asset = TestAssets.CreateAsset();
        var prop = new ObjectPropertyData(new FName(asset, "BaseColor")) { Value = FPackageIndex.FromRawIndex(0) };

        var ok = PropertyValueAccessor.TrySetStringValue(prop, "/Game/Textures/T_DoesNotExist.T_DoesNotExist", asset);

        Assert.False(ok);
    }

    [Fact]
    public void UpdateIsZeroFlag_ReflectsEmptyString()
    {
        var asset = TestAssets.CreateAsset();
        var prop = new StrPropertyData(new FName(asset, "DisplayName")) { Value = new FString(""), IsZero = false };

        PropertyValueAccessor.UpdateIsZeroFlag(prop);

        Assert.True(prop.IsZero);
    }

    [Fact]
    public void AsSearchableString_ReadsIntPointAsCommaPair()
    {
        var asset = TestAssets.CreateAsset();
        var prop = new IntPointPropertyData(new FName(asset, "DropCount")) { Value = [2, 5] };

        Assert.Equal("2,5", PropertyValueAccessor.AsSearchableString(prop, asset));
    }

    [Fact]
    public void TrySetStringValue_SetsIntPointFromCommaPair()
    {
        var asset = TestAssets.CreateAsset();
        var prop = new IntPointPropertyData(new FName(asset, "DropCount")) { Value = [0, 0] };

        var ok = PropertyValueAccessor.TrySetStringValue(prop, "3,7", asset);

        Assert.True(ok);
        Assert.Equal([3, 7], prop.Value);
    }

    [Fact]
    public void TrySetStringValue_RejectsMalformedIntPoint()
    {
        var asset = TestAssets.CreateAsset();
        var prop = new IntPointPropertyData(new FName(asset, "DropCount")) { Value = [1, 1] };

        var ok = PropertyValueAccessor.TrySetStringValue(prop, "not-a-pair", asset);

        Assert.False(ok);
        Assert.Equal([1, 1], prop.Value);
    }

    [Fact]
    public void AsSearchableString_ReadsPlainByteAsNumber()
    {
        var asset = TestAssets.CreateAsset();
        var prop = new BytePropertyData(new FName(asset, "Rarity")) { ByteType = BytePropertyType.Byte, Value = 3 };

        Assert.Equal("3", PropertyValueAccessor.AsSearchableString(prop, asset));
    }

    [Fact]
    public void AsSearchableString_ReadsEnumBackedByteByName()
    {
        var asset = TestAssets.CreateAsset();
        var prop = new BytePropertyData(new FName(asset, "Rarity"))
        {
            ByteType = BytePropertyType.FName,
            EnumType = new FName(asset, "ERarity"),
            EnumValue = new FName(asset, "Legendary"),
        };

        Assert.Equal("Legendary", PropertyValueAccessor.AsSearchableString(prop, asset));
    }

    [Fact]
    public void TrySetStringValue_SetsPlainByteFromNumber()
    {
        var asset = TestAssets.CreateAsset();
        var prop = new BytePropertyData(new FName(asset, "Rarity")) { ByteType = BytePropertyType.Byte, Value = 0 };

        var ok = PropertyValueAccessor.TrySetStringValue(prop, "9", asset);

        Assert.True(ok);
        Assert.Equal((byte)9, prop.Value);
    }

    [Fact]
    public void TrySetStringValue_SetsEnumBackedByteByName()
    {
        var asset = TestAssets.CreateAsset();
        var prop = new BytePropertyData(new FName(asset, "Rarity"))
        {
            ByteType = BytePropertyType.FName,
            EnumType = new FName(asset, "ERarity"),
            EnumValue = new FName(asset, "Common"),
        };

        var ok = PropertyValueAccessor.TrySetStringValue(prop, "Legendary", asset);

        Assert.True(ok);
        Assert.Equal("Legendary", PropertyValueAccessor.AsSearchableString(prop, asset));
    }
}
