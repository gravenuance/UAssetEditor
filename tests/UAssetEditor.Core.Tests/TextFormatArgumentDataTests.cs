using System.IO;
using System.Text;
using UAssetAPI;
using UAssetAPI.CustomVersions;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.UnrealTypes.EngineEnums;

namespace UAssetEditor.Core.Tests;

public class TextFormatArgumentDataTests
{
    [Fact]
    public void Read_WithoutVariantCustomVersion_ReadsNameThenBareText()
    {
        // Days Gone (UE4.17) records no custom versions, so an argument is FString + FText with no type byte.
        var asset = AssetWithoutCustomVersions();
        var bytes = LegacyArgument("0", "job_obj_BE_AZ_03_obj_01");

        var argument = ReadArgument(asset, bytes, out long consumed);

        Assert.Equal(bytes.Length, consumed);
        Assert.Equal("0", argument.ArgumentName.Value);
        Assert.Equal(EFormatArgumentType.Text, argument.ArgumentValue.Type);
        var text = Assert.IsType<TextPropertyData>(argument.ArgumentValue.Value);
        Assert.Equal("job_obj_BE_AZ_03_obj_01", text.Value.Value);
    }

    [Fact]
    public void Write_WithoutVariantCustomVersion_ReproducesLegacyBytes()
    {
        var asset = AssetWithoutCustomVersions();
        var bytes = LegacyArgument("1", ": ");

        var argument = ReadArgument(asset, bytes, out _);

        Assert.Equal(bytes, WriteArgument(asset, argument));
    }

    [Theory]
    [InlineData(EngineVersion.VER_UE4_27)]
    [InlineData(EngineVersion.VER_UE5_3)] // unversioned games such as Marvel Rivals take their custom versions from here
    public void RoundTrip_WithVariantCustomVersion_KeepsTypeByte(EngineVersion engine)
    {
        var asset = new UAsset(engine);
        var original = new FFormatArgumentData(new FString("Count"), new FFormatArgumentValue(EFormatArgumentType.Float, 2.5f));

        var bytes = WriteArgument(asset, original);
        var argument = ReadArgument(asset, bytes, out long consumed);

        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(EFormatArgumentType.Float, argument.ArgumentValue.Type);
        Assert.Equal(2.5f, argument.ArgumentValue.Value);
    }

    private static UAsset AssetWithoutCustomVersions() =>
        new(EngineVersion.VER_UE4_17) { CustomVersionContainer = [] };

    private static FFormatArgumentData ReadArgument(UAsset asset, byte[] bytes, out long consumed)
    {
        using var reader = new AssetBinaryReader(new MemoryStream(bytes), asset);
        var argument = new FFormatArgumentData(reader);
        consumed = reader.BaseStream.Position;
        return argument;
    }

    private static byte[] WriteArgument(UAsset asset, FFormatArgumentData argument)
    {
        using var stream = new MemoryStream();
        using (var writer = new AssetBinaryWriter(stream, Encoding.ASCII, leaveOpen: true, asset))
            argument.Write(writer);
        return stream.ToArray();
    }

    /// <summary>Name, then an FText: flags, Base history, empty namespace, key and source string.</summary>
    private static byte[] LegacyArgument(string name, string text)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            WriteFString(writer, name);
            writer.Write((int)ETextFlag.Immutable);
            writer.Write((sbyte)TextHistoryType.Base);
            writer.Write(0);
            WriteFString(writer, text);
            WriteFString(writer, text);
        }
        return stream.ToArray();
    }

    private static void WriteFString(BinaryWriter writer, string value)
    {
        writer.Write(value.Length + 1);
        writer.Write(Encoding.ASCII.GetBytes(value));
        writer.Write((byte)0);
    }
}
