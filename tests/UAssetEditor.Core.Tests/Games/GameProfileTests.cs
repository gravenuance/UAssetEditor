using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.Games;

namespace UAssetEditor.Core.Tests.Games;

public class GameProfileTests
{
    [Theory]
    [InlineData("rivals", Game.MarvelRivals)]
    [InlineData("RIVALS", Game.MarvelRivals)]
    [InlineData(" ff7r ", Game.FinalFantasy7Remake)]
    [InlineData("Ff7R", Game.FinalFantasy7Remake)]
    public void FindByCliName_IsCaseInsensitive(string name, Game expected)
    {
        Assert.Equal(expected, GameProfile.FindByCliName(name)?.Game);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("nope")]
    [InlineData("")]
    [InlineData(null)]
    public void FindByCliName_ReturnsNullForAnythingElse(string? name)
    {
        Assert.Null(GameProfile.FindByCliName(name));
    }

    [Fact]
    public void Rivals_CarriesItsEngineVersionAndKey()
    {
        var profile = GameProfile.For(Game.MarvelRivals)!;

        Assert.Equal("rivals", profile.CliName);
        Assert.Equal("Marvel Rivals", profile.DisplayName);
        Assert.Equal(EngineVersion.VER_UE5_3, profile.EngineVersion);
        Assert.Equal("0x0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74", profile.AesKeyHex);
    }

    [Fact]
    public void Ff7r_CarriesItsEngineVersionAndKey()
    {
        var profile = GameProfile.For(Game.FinalFantasy7Remake)!;

        Assert.Equal("ff7r", profile.CliName);
        Assert.Equal("Final Fantasy VII Remake", profile.DisplayName);
        Assert.Equal(EngineVersion.VER_UE4_18, profile.EngineVersion);
        Assert.Equal("0x23989837645C9D28BA58072B2076E895B853A7C9E1C5591B814C4FD2A2D7B782", profile.AesKeyHex);
    }

    [Fact]
    public void None_HasNoProfile()
    {
        Assert.Null(GameProfile.For(Game.None));
        Assert.Equal("rivals, ff7r", GameProfile.ValidNames);
    }

    [Fact]
    public void ResolveEngineVersion_PrefersExplicitThenProfileThenDefault()
    {
        var ff7r = GameProfile.For(Game.FinalFantasy7Remake);

        Assert.Equal(EngineVersion.VER_UE5_1, GameProfile.ResolveEngineVersion(ff7r, EngineVersion.VER_UE5_1));
        Assert.Equal(EngineVersion.VER_UE4_18, GameProfile.ResolveEngineVersion(ff7r, null));
        Assert.Equal(EngineVersion.VER_UE4_27, GameProfile.ResolveEngineVersion(null, null));
        Assert.Equal(EngineVersion.VER_UE5_1, GameProfile.ResolveEngineVersion(null, EngineVersion.VER_UE5_1));
    }

    [Fact]
    public void ResolveAesKeyHex_PrefersExplicitThenProfileThenEmpty()
    {
        var rivals = GameProfile.For(Game.MarvelRivals);

        Assert.Equal("0xABCD", GameProfile.ResolveAesKeyHex(rivals, "0xABCD"));
        Assert.Equal("0x0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74", GameProfile.ResolveAesKeyHex(rivals, null));
        Assert.Equal("0x0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74", GameProfile.ResolveAesKeyHex(rivals, "  "));
        Assert.Equal(string.Empty, GameProfile.ResolveAesKeyHex(null, null));
    }
}
