using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.Editing;

namespace UAssetEditor.Core.Tests;

public class VersionStripperTests
{
    [Fact]
    public void Strip_MarksTheSummaryUnversioned_AndKeepsTaggedProperties()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);

        VersionStripper.Strip(asset);

        Assert.True(asset.IsUnversioned);
        Assert.False(asset.HasUnversionedProperties);
    }

    [Fact]
    public void Strip_RefusesAPackageWithUnversionedProperties()
    {
        var asset = TestAssets.CreateAsset();
        asset.PackageFlags |= EPackageFlags.PKG_UnversionedProperties;

        Assert.Throws<ArgumentException>(() => VersionStripper.Strip(asset));
    }

    [Fact]
    public void Strip_IsIdempotent()
    {
        var asset = TestAssets.CreateAsset();

        VersionStripper.Strip(asset);
        VersionStripper.Strip(asset);

        Assert.True(asset.IsUnversioned);
    }
}
