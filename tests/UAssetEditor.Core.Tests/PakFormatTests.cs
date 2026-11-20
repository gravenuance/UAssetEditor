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
        var bytes = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");

        var fromBytes = PakFormat.Of(bytes, PakVariant.Standard);

        Assert.Equal(fromBytes, PakFormat.Resolve(null, "0x00112233445566778899aabbccddeeff"));
        Assert.Equal(fromBytes, PakFormat.Resolve(null, " 00112233445566778899AABBCCDDEEFF "));
        Assert.Equal(PakFormat.Of(null, PakVariant.Standard), PakFormat.Resolve(null, ""));
    }

    [Fact]
    public void TwoFormats_DifferByKey_OrByVariant()
    {
        var format = PakFormat.Of([1, 2, 3], PakVariant.Standard);

        Assert.NotEqual(format, PakFormat.Of([1, 2, 4], PakVariant.Standard));
        Assert.NotEqual(format, PakFormat.Of(null, PakVariant.Standard));
        Assert.NotEqual(format, PakFormat.Of([1, 2, 3], PakVariant.MarvelRivals));
    }

    [Fact]
    public void TheKeyBytes_CannotBeChangedThroughTheFormat()
    {
        var format = PakFormat.Of([1, 2, 3], PakVariant.Standard);

        format.CopyAesKey()![0] = 9;

        Assert.Equal(new byte[] { 1, 2, 3 }, format.CopyAesKey());
    }
}
