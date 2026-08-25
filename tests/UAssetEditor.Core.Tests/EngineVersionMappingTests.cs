using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.AssetSources.IoStore;

namespace UAssetEditor.Core.Tests;

public class EngineVersionMappingTests
{
    [Theory]
    [InlineData(EngineVersion.VER_UE4_25, "UE4_25")]
    [InlineData(EngineVersion.VER_UE4_27, "UE4_27")]
    [InlineData(EngineVersion.VER_UE5_0, "UE5_0")]
    [InlineData(EngineVersion.VER_UE5_3, "UE5_3")]
    public void ToRetocVersion_ForRetocSupportedVersion_ReturnsMatchingString(EngineVersion version, string expected) =>
        Assert.Equal(expected, EngineVersionMapping.ToRetocVersion(version));

    [Fact]
    public void ToRetocVersion_ForVersionOlderThanIoStoreSupport_ReturnsNull()
    {
        // IoStore itself doesn't exist before UE4.25 - retoc's own --version enum starts there,
        // so anything older genuinely has no equivalent, not just an unmapped one.
        Assert.Null(EngineVersionMapping.ToRetocVersion(EngineVersion.VER_UE4_11));
    }

    [Fact]
    public void ToRetocVersion_ForNonReleaseSentinel_ReturnsNull() =>
        Assert.Null(EngineVersionMapping.ToRetocVersion(EngineVersion.UNKNOWN));
}
