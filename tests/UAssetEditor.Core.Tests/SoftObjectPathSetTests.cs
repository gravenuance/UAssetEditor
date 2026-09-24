using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.PropertyAccess;

namespace UAssetEditor.Core.Tests;

public class SoftObjectPathSetTests
{
    private static SoftObjectPropertyData SoftRef(UAssetAPI.UAsset asset, string package, string assetName) =>
        new(new FName(asset, "Materials_SoftPtr"))
        {
            Value = new FSoftObjectPath(new FName(asset, package), new FName(asset, assetName), null),
        };

    [Fact]
    public void Set_PointsTheReferenceAtTheNewAsset()
    {
        var asset = TestAssets.CreateAsset();
        var prop = SoftRef(asset, "/Game/Old/MI_Old", "MI_Old");

        Assert.True(PropertyValueAccessor.TrySetStringValue(prop, "/Game/New/MI_New.MI_New", asset));

        Assert.Equal("/Game/New/MI_New.MI_New", PropertyValueAccessor.AsSearchableString(prop, asset));
    }

    [Fact]
    public void Set_KeepsASubObjectPathAfterTheColon()
    {
        var asset = TestAssets.CreateAsset();
        var prop = SoftRef(asset, "/Game/Old/BP_Old", "BP_Old");

        Assert.True(PropertyValueAccessor.TrySetStringValue(prop, "/Game/New/BP_New.BP_New:Mesh", asset));

        Assert.Equal("/Game/New/BP_New.BP_New:Mesh", PropertyValueAccessor.AsSearchableString(prop, asset));
    }

    [Theory]
    [InlineData("MI_New")]
    [InlineData("/Game/New/MI_New")]
    [InlineData("/Game/New/MI_New.")]
    [InlineData(".MI_New")]
    public void Set_RejectsTextThatIsNotAnAssetPath_AndLeavesTheValue(string text)
    {
        var asset = TestAssets.CreateAsset();
        var prop = SoftRef(asset, "/Game/Old/MI_Old", "MI_Old");

        Assert.False(PropertyValueAccessor.TrySetStringValue(prop, text, asset));

        Assert.Equal("/Game/Old/MI_Old.MI_Old", PropertyValueAccessor.AsSearchableString(prop, asset));
    }

    [Fact]
    public void Set_RegistersTheNewPathOnce_WhenThePackageKeepsASoftPathList()
    {
        // UE 5.1+ packages serialise soft paths as indices into this list; a path missing from it cannot be saved.
        var asset = TestAssets.CreateAsset();
        var prop = SoftRef(asset, "/Game/Old/MI_Old", "MI_Old");
        asset.SoftObjectPathList = [prop.Value];

        Assert.True(PropertyValueAccessor.TrySetStringValue(prop, "/Game/New/MI_New.MI_New", asset));
        Assert.True(PropertyValueAccessor.TrySetStringValue(SoftRef(asset, "/Game/Old/MI_Old", "MI_Old"), "/Game/New/MI_New.MI_New", asset));

        Assert.Equal(2, asset.SoftObjectPathList.Count);
        Assert.Contains(prop.Value, asset.SoftObjectPathList);
    }

    [Fact]
    public void Set_DoesNotCreateASoftPathList_WhenThePackageHasNone()
    {
        var asset = TestAssets.CreateAsset();
        var prop = SoftRef(asset, "/Game/Old/MI_Old", "MI_Old");

        Assert.True(PropertyValueAccessor.TrySetStringValue(prop, "/Game/New/MI_New.MI_New", asset));

        Assert.True(asset.SoftObjectPathList is null or { Count: 0 });
    }
}
