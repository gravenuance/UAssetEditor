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

    public static TheoryData<string, string, string> SizedIntegers => new()
    {
        { "Int8", "-128", "128" },
        { "Int16", "32767", "32768" },
        { "UInt16", "65535", "-1" },
        { "UInt32", "4294967295", "-1" },
        { "UInt64", "18446744073709551615", "-1" },
    };

    [Theory]
    [MemberData(nameof(SizedIntegers))]
    public void SizedIntegers_RoundTripAtTheirLimitsAndRejectOutOfRange(string kind, string limit, string outOfRange)
    {
        var asset = TestAssets.CreateAsset();
        var prop = CreateSizedInteger(asset, kind);

        Assert.True(prop.IsZero);
        Assert.True(PropertyValueAccessor.TrySetStringValue(prop, limit, asset));
        PropertyValueAccessor.UpdateIsZeroFlag(prop);

        Assert.Equal(limit, PropertyValueAccessor.AsSearchableString(prop, asset));
        Assert.False(prop.IsZero);
        Assert.False(PropertyValueAccessor.TrySetStringValue(prop, outOfRange, asset));
        Assert.Equal(limit, PropertyValueAccessor.AsSearchableString(prop, asset));
    }

    public static TheoryData<string, string, string> VectorKinds => new()
    {
        { "Vector", "0,0,-980.5", "1,2" },
        { "Rotator", "10,90,-45", "1,2,3,4" },
        { "Quat", "0,0,0.7071,0.7071", "1,2,3" },
        { "Vector2D", "3.25,-1", "1" },
        { "Vector4", "1,2,3,4", "1,2,3,x" },
    };

    [Theory]
    [MemberData(nameof(VectorKinds))]
    public void VectorKinds_RoundTripAsCommaSeparatedComponents(string kind, string value, string malformed)
    {
        var asset = TestAssets.CreateAsset();
        var name = new FName(asset, "Value");
        PropertyData prop = kind switch
        {
            "Vector" => new VectorPropertyData(name) { IsZero = true },
            "Rotator" => new RotatorPropertyData(name) { IsZero = true },
            "Quat" => new QuatPropertyData(name) { IsZero = true },
            "Vector2D" => new Vector2DPropertyData(name) { IsZero = true },
            "Vector4" => new Vector4PropertyData(name) { IsZero = true },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Assert.True(PropertyValueAccessor.TrySetStringValue(prop, value, asset));
        PropertyValueAccessor.UpdateIsZeroFlag(prop);

        Assert.Equal(value, PropertyValueAccessor.AsSearchableString(prop, asset));
        Assert.False(prop.IsZero);
        Assert.False(PropertyValueAccessor.TrySetStringValue(prop, malformed, asset));
        Assert.Equal(value, PropertyValueAccessor.AsSearchableString(prop, asset));
    }

    private static PropertyData CreateSizedInteger(UAsset asset, string kind)
    {
        var name = new FName(asset, "Value");
        return kind switch
        {
            "Int8" => new Int8PropertyData(name) { IsZero = true },
            "Int16" => new Int16PropertyData(name) { IsZero = true },
            "UInt16" => new UInt16PropertyData(name) { IsZero = true },
            "UInt32" => new UInt32PropertyData(name) { IsZero = true },
            "UInt64" => new UInt64PropertyData(name) { IsZero = true },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
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
    public void TrySetStringValue_KeepsANameThatIsInTheMapVerbatim()
    {
        var asset = TestAssets.CreateAsset();
        asset.AddNameReference(new FString("Socket"));
        asset.AddNameReference(new FString("Socket_1"));
        var prop = new NamePropertyData(new FName(asset, "Value")) { Value = new FName(asset, "Socket") };

        Assert.True(PropertyValueAccessor.TrySetStringValue(prop, "Socket_1", asset));

        Assert.Equal("Socket_1", prop.Value.Value.Value);
        Assert.Equal(0, prop.Value.Number);
    }

    [Theory]
    [InlineData("Socket_01")] // a leading zero is part of the string in Unreal, not a number
    [InlineData("Missing_3")] // base not in the map: keep today's behaviour
    public void TrySetStringValue_DoesNotSplitWhatUnrealWouldNot(string text)
    {
        var asset = TestAssets.CreateAsset();
        asset.AddNameReference(new FString("Socket"));
        var prop = new NamePropertyData(new FName(asset, "Value")) { Value = new FName(asset, "Socket") };

        Assert.True(PropertyValueAccessor.TrySetStringValue(prop, text, asset));

        Assert.Equal(text, prop.Value.Value.Value);
        Assert.Equal(0, prop.Value.Number);
    }

    [Fact]
    public void EnumValue_DisplaysWithItsNumberSuffix()
    {
        var asset = TestAssets.CreateAsset();
        var prop = new EnumPropertyData(new FName(asset, "Mode"))
        {
            EnumType = new FName(asset, "EMode"),
            Value = new FName(asset, "Slot", 3),
        };

        Assert.Equal("Slot_2", PropertyValueAccessor.AsSearchableString(prop, asset));
    }

    [Fact]
    public void AsSearchableString_NamesAnExportReferenceWithItsNumberSuffix()
    {
        // Behaviour-tree siblings share one base name and differ only by FName number.
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset, "SBBTTask_SbUseSkill");
        TestAssets.CreateSampleExport(asset, "SBBTTask_SbUseSkill").ObjectName = new FName(asset, "SBBTTask_SbUseSkill", 143);
        var prop = new ObjectPropertyData(new FName(asset, "ChildTask")) { Value = FPackageIndex.FromExport(1) };

        Assert.Equal("SBBTTask_SbUseSkill_142", PropertyValueAccessor.AsSearchableString(prop, asset));
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
