using UAssetAPI;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Games;
using UAssetEditor.Tooling;

namespace UAssetEditor.Core.Tests;

/// <summary>The one place a typed pak key is checked: every front end (CLI, app, MCP) and every format goes through it.</summary>
public sealed class PakAesKeyTests
{
    // gitleaks:allow (made-up test keys)
    private const string ValidHex = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF";
    private const string TooShortHex = "00112233445566778899AABBCCDDEEFF"; // 16 bytes
    private const string TooLongHex = ValidHex + "00"; // 33 bytes
    private const string OddLengthHex = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEF"; // 63 digits

    private static readonly byte[] ValidBytes = Convert.FromHexString(ValidHex);

    [Theory]
    [InlineData(ValidHex)]
    [InlineData("0x" + ValidHex)]
    [InlineData("0X" + ValidHex)]
    [InlineData("  " + ValidHex + "  ")]
    [InlineData("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff")]
    public void A32ByteKey_IsAccepted_HoweverItIsSpelt(string text)
    {
        Assert.Equal(ValidBytes, PakAesKey.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0x")]
    public void AnEmptyKey_MeansNoKey(string text)
    {
        Assert.Null(PakAesKey.Parse(text));
    }

    [Theory]
    [InlineData(TooShortHex)]
    [InlineData("00112233")]
    [InlineData(TooLongHex)]
    [InlineData(OddLengthHex)]
    [InlineData("abc")]
    [InlineData("not hex")]
    [InlineData("0xZZ")]
    public void AKeyThatIsNotExactly32BytesOfHex_IsRefused_WithOneMessage_NotRepeatingTheKey(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var error = Assert.Throws<FormatException>(() => PakAesKey.Parse(text));

        Assert.Equal(PakAesKey.Requirement, error.Message);
        Assert.Contains("32 bytes", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(text.Trim(), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(TooShortHex)]
    [InlineData(OddLengthHex)]
    public void TheFormatReadingTheKeyText_RefusesItToo_ForEveryVariant(string text)
    {
        Assert.Equal(PakAesKey.Requirement, Assert.Throws<FormatException>(() => PakFormat.Resolve(null, text)).Message);
        Assert.Equal(PakAesKey.Requirement, Assert.Throws<FormatException>(() => PakFormat.Resolve(GameProfile.For(Game.MarvelRivals), text)).Message);
    }

    [Fact]
    public void ARivalsKeyOfTheWrongLength_GetsTheLengthMessage_NotOneAboutWords()
    {
        // 10 hex digits is 5 bytes: not a whole number of 4-byte words, which used to surface as a different message.
        var error = Assert.Throws<FormatException>(() => PakFormat.Resolve(GameProfile.For(Game.MarvelRivals), "0102030405"));

        Assert.Equal(PakAesKey.Requirement, error.Message);
    }

    [Theory]
    [InlineData(PakVariant.Standard)]
    [InlineData(PakVariant.MarvelRivals)]
    public void AFormatBuiltFromKeyBytes_RefusesABadLength(PakVariant variant)
    {
        Assert.Throws<ArgumentException>(() => PakFormat.Of([1, 2, 3, 4, 5], variant));
        Assert.Throws<ArgumentException>(() => PakFormat.Of(Convert.FromHexString(TooShortHex), variant));
        Assert.Throws<ArgumentException>(() => PakFormat.Of(Convert.FromHexString(TooLongHex), variant));
    }

    [Fact]
    public void TheCliKeyOption_GoesThroughTheSameCheck()
    {
        var args = new ArgReader(["--aes", TooShortHex]);

        Assert.Equal(PakAesKey.Requirement, Assert.Throws<FormatException>(() => AssetIo.ResolveAesKey(args)).Message);
        Assert.Equal(ValidBytes, AssetIo.ResolveAesKey(new ArgReader(["--aes", ValidHex])));
    }

    [Fact]
    public void AValid32ByteKey_HasACipherKey_ForEveryVariant_WithoutAnyThrow()
    {
        Assert.Equal(ValidBytes, PakFormat.Of(ValidBytes, PakVariant.Standard).CopyCipherKey());
        Assert.Equal(32, PakFormat.Of(ValidBytes, PakVariant.MarvelRivals).CopyCipherKey()!.Length);
    }
}
