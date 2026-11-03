using System.IO;
using System.Text;
using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.Tests;

public class OptionalPropertyDataTests
{
    [Fact]
    public void RoundTrip_SetValue_KeepsTheInnerValue()
    {
        var asset = TestAssets.CreateAsset();
        var original = new OptionalPropertyData(new FName(asset, "MaybeCount")) { Value = new IntPropertyData(new FName(asset, "0")) { Value = 7 } };

        var bytes = Write(asset, original);
        var read = Read(asset, bytes, out var consumed);

        Assert.Equal(bytes.Length, consumed);
        Assert.Equal("IntProperty", read.InnerType.Value.Value);
        Assert.Equal(7, Assert.IsType<IntPropertyData>(read.Value).Value);
        // bIsSet as a 32-bit bool, then the int itself.
        Assert.Equal(new byte[] { 1, 0, 0, 0, 7, 0, 0, 0 }, bytes[^8..]);
    }

    [Fact]
    public void Read_UnsetValue_LeavesValueNull()
    {
        var asset = TestAssets.CreateAsset();
        var original = new OptionalPropertyData(new FName(asset, "MaybeCount")) { Value = new IntPropertyData(new FName(asset, "0")) { Value = 7 } };
        var bytes = Write(asset, original);
        bytes[^8] = 0; // clear bIsSet; the reader must stop before the int

        var read = Read(asset, bytes[..^4], out var consumed);

        Assert.Equal(bytes.Length - 4, consumed);
        Assert.Null(read.Value);
    }

    private static byte[] Write(UAsset asset, OptionalPropertyData property)
    {
        using var stream = new MemoryStream();
        using (var writer = new AssetBinaryWriter(stream, Encoding.ASCII, leaveOpen: true, asset))
            property.Write(writer, includeHeader: true);
        return stream.ToArray();
    }

    private static OptionalPropertyData Read(UAsset asset, byte[] bytes, out long consumed)
    {
        using var reader = new AssetBinaryReader(new MemoryStream(bytes), asset);
        var property = new OptionalPropertyData(new FName(asset, "MaybeCount"));
        property.Read(reader, includeHeader: true, leng1: bytes.Length);
        consumed = reader.BaseStream.Position;
        return property;
    }
}
