using UAssetAPI;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Games;

namespace UAssetEditor.Core.Tests;

/// <summary>The (key, variant) pair every pak open, reload and repack decision is taken from.</summary>
public sealed class PakFormatTests
{
    private const string RivalsKeyHex = "0x0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74";

    [Fact]
    public void WithNoGameSelected_TheVariantAlreadyInUseIsKept_AsTheKeyIs()
    {
        // "None" leaves the key field as it is, so it must leave the variant of the open pak alone as well.
        var format = PakFormat.Resolve(null, RivalsKeyHex, PakVariant.MarvelRivals);

        Assert.Equal(PakVariant.MarvelRivals, format.Variant);
        Assert.Equal(Convert.FromHexString(RivalsKeyHex[2..]), format.CopyAesKey());
    }

    [Fact]
    public void WithNoGameSelected_AndNothingToKeep_TheFormatIsStandard()
    {
        Assert.Equal(PakVariant.Standard, PakFormat.Resolve(null, "").Variant);
    }

    [Theory]
    [InlineData(Game.MarvelRivals, PakVariant.Standard, PakVariant.MarvelRivals)]
    [InlineData(Game.FinalFantasy7Remake, PakVariant.MarvelRivals, PakVariant.Standard)]
    [InlineData(Game.StellarBlade, PakVariant.MarvelRivals, PakVariant.Standard)]
    public void WithAGameSelected_TheGamesVariantWins_OverTheOneKept(Game game, PakVariant kept, PakVariant expected)
    {
        Assert.Equal(expected, PakFormat.Resolve(GameProfile.For(game), "", kept).Variant);
    }

    [Fact]
    public void AnEmptyKey_MeansNoKey()
    {
        Assert.Null(PakFormat.Resolve(null, "  ").CopyAesKey());
        Assert.Null(PakFormat.Resolve(null, null).CopyAesKey());
        Assert.Null(PakFormat.Of(null, PakVariant.Standard).CopyAesKey());
    }

    [Fact]
    public void AKeyThatIsNotHex_IsRefused()
    {
        Assert.Throws<FormatException>(() => PakFormat.Resolve(null, "not hex"));
    }

    [Fact]
    public void TwoFormats_AreEqualWhenKeyBytesAndVariantAre_HoweverTheKeyWasSpelt()
    {
        var bytes = Convert.FromHexString("00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF");

        var fromBytes = PakFormat.Of(bytes, PakVariant.Standard);

        Assert.Equal(fromBytes, PakFormat.Resolve(null, "0x00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"));
        Assert.Equal(fromBytes, PakFormat.Resolve(null, " 00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF "));
        Assert.Equal(PakFormat.Of(null, PakVariant.Standard), PakFormat.Resolve(null, ""));
    }

    [Fact]
    public void TwoFormats_DifferByKey_OrByVariant()
    {
        var format = PakFormat.Of(KeyStartingWith(1, 2, 3), PakVariant.Standard);

        Assert.NotEqual(format, PakFormat.Of(KeyStartingWith(1, 2, 4), PakVariant.Standard));
        Assert.NotEqual(format, PakFormat.Of(null, PakVariant.Standard));
        Assert.NotEqual(format, PakFormat.Of(KeyStartingWith(1, 2, 3), PakVariant.MarvelRivals));
    }

    [Fact]
    public void ForMarvelRivals_TheCipherKeyIsTheProfileKey_WithEach4ByteWordReversed()
    {
        // Hand-derived: 0C 26 3D 8C -> 8C 3D 26 0C, 22 DC B0 85 -> 85 B0 DC 22, and so on for all eight words.
        const string PakCipherKeyHex = "8C3D260C85B0DC22C3994889836379A3E09DBFE9C908FBCB2EEF2DBF749DF284";

        var format = PakFormat.Resolve(GameProfile.For(Game.MarvelRivals), RivalsKeyHex);

        Assert.Equal(Convert.FromHexString(PakCipherKeyHex), format.CopyCipherKey());
        Assert.Equal(Convert.FromHexString(RivalsKeyHex[2..]), format.CopyAesKey());
    }

    [Fact]
    public void ForAStandardPak_TheCipherKeyIsTheKeyAsGiven()
    {
        var format = PakFormat.Of(Convert.FromHexString("00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF"), PakVariant.Standard);

        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF"), format.CopyCipherKey());
    }

    [Fact]
    public void WithNoKey_ThereIsNoCipherKey_InEveryVariant()
    {
        Assert.Null(PakFormat.Of(null, PakVariant.MarvelRivals).CopyCipherKey());
        Assert.Null(PakFormat.Of([], PakVariant.MarvelRivals).CopyCipherKey());
        Assert.Null(PakFormat.Of(null, PakVariant.Standard).CopyCipherKey());
    }

    [Fact]
    public void TheCipherKeyBytes_CannotBeChangedThroughTheFormat()
    {
        var format = PakFormat.Of(KeyStartingWith(1, 2, 3, 4), PakVariant.MarvelRivals);

        format.CopyCipherKey()![0] = 9;

        Assert.Equal(KeyStartingWith(4, 3, 2, 1), format.CopyCipherKey());
    }

    [Fact]
    public void TheKeyBytes_CannotBeChangedThroughTheFormat()
    {
        var format = PakFormat.Of(KeyStartingWith(1, 2, 3), PakVariant.Standard);

        format.CopyAesKey()![0] = 9;

        Assert.Equal(KeyStartingWith(1, 2, 3), format.CopyAesKey());
    }

    /// <summary>A 32-byte key whose first bytes are <paramref name="head"/> and the rest zero.</summary>
    private static byte[] KeyStartingWith(params byte[] head)
    {
        var key = new byte[32];
        head.CopyTo(key, 0);
        return key;
    }
}
