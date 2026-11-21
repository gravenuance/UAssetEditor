using UAssetAPI;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Games;

namespace UAssetEditor.Core.Tests;

/// <summary>When an open pak must be reopened after a key or game change, and what a bad key does to that decision.</summary>
public sealed class PakReloadDecisionTests
{
    private const string KeyHex = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF";
    private const string OtherKeyHex = "FFEEDDCCBBAA99887766554433221100FFEEDDCCBBAA99887766554433221100";
    private static readonly byte[] KeyBytes = Convert.FromHexString(KeyHex);

    [Fact]
    public void APakAlreadyInTheWantedFormat_IsNotReopened()
    {
        var decision = PakReloadDecision.For(PakFormat.Of(KeyBytes, PakVariant.Standard), null, KeyHex);

        Assert.False(decision.NeedsReopen);
        Assert.Null(decision.ReopenAs);
        Assert.Null(decision.KeyProblem);
        Assert.False(decision.OpenPakDiffersFromSettings);
    }

    [Theory]
    [InlineData("0x00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff")]
    [InlineData("  00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF  ")]
    public void TheSameKeySpeltDifferently_IsNotAReason_ToReopen(string keyText)
    {
        var decision = PakReloadDecision.For(PakFormat.Of(KeyBytes, PakVariant.Standard), null, keyText);

        Assert.False(decision.NeedsReopen);
    }

    [Fact]
    public void ADifferentKey_ReopensInTheNewKey_KeepingTheOpenVariant()
    {
        var decision = PakReloadDecision.For(PakFormat.Of(KeyBytes, PakVariant.MarvelRivals), null, OtherKeyHex);

        Assert.True(decision.NeedsReopen);
        Assert.Equal(PakFormat.Of(Convert.FromHexString(OtherKeyHex), PakVariant.MarvelRivals), decision.ReopenAs);
        Assert.Null(decision.KeyProblem);
    }

    [Fact]
    public void ClearingTheKey_ReopensWithoutOne()
    {
        var decision = PakReloadDecision.For(PakFormat.Of(KeyBytes, PakVariant.Standard), null, "");

        Assert.Equal(PakFormat.Of(null, PakVariant.Standard), decision.ReopenAs);
    }

    [Fact]
    public void SelectingAGameWithAnotherVariant_ReopensInThatVariant()
    {
        var rivals = GameProfile.For(Game.MarvelRivals)!;

        var decision = PakReloadDecision.For(PakFormat.Of(PakAesKey.Parse(rivals.AesKeyHex), PakVariant.Standard), rivals, rivals.AesKeyHex);

        Assert.Equal(PakFormat.Of(PakAesKey.Parse(rivals.AesKeyHex), PakVariant.MarvelRivals), decision.ReopenAs);
    }

    [Fact]
    public void WithNoGameSelected_AVariantPakIsNotReopenedAsStandard()
    {
        var decision = PakReloadDecision.For(PakFormat.Of(KeyBytes, PakVariant.MarvelRivals), null, KeyHex);

        Assert.False(decision.NeedsReopen);
    }

    [Theory]
    [InlineData("not hex")]
    [InlineData("0xABC")]
    [InlineData("00112233445566778899AABBCCDDEEFF")] // 16 bytes
    public void AKeyThatIsNotAValidKey_IsReported_NotThrown_AndTheOpenPakIsKept(string keyText)
    {
        var decision = PakReloadDecision.For(PakFormat.Of(KeyBytes, PakVariant.Standard), null, keyText);

        Assert.False(decision.NeedsReopen);
        Assert.Equal(PakAesKey.Requirement, decision.KeyProblem);
        Assert.True(decision.OpenPakDiffersFromSettings);
    }
}
