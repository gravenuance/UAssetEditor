using System.IO;
using UAssetAPI.UnrealTypes;
using UAssetEditor.App.ViewModels;
using UAssetEditor.Core.Games;

namespace UAssetEditor.App.Tests;

public sealed class ResolverSettingsTests
{
    private static ResolverSettings Of(string? usmapPath, bool loaded = false, EngineVersion version = EngineVersion.VER_UE5_3, Game game = Game.None) =>
        ResolverSettings.Create(version, game, usmapPath, loaded);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankUsmapPath_IsNone(string? blank)
    {
        Assert.Null(Of(blank).UsmapPath);
        Assert.True(Of(blank).SameInputs(Of(null)));
    }

    [Fact]
    public void ARelativeUsmapPath_IsKeptAsItsFullPath()
    {
        Assert.Equal(Path.GetFullPath("maps/Game.usmap"), Of(" maps/Game.usmap ").UsmapPath);
    }

    [Fact]
    public void UsmapPathsThatDifferOnlyInCaseOrSpelling_AreTheSameInput()
    {
        var full = Path.GetFullPath("maps/Game.usmap");

        Assert.True(Of(full).SameInputs(Of("maps/GAME.usmap")));
        Assert.True(Of(full).SameInputs(Of("maps/../maps/Game.usmap")));
    }

    [Fact]
    public void DifferentUsmapFiles_AreDifferentInputs()
    {
        Assert.False(Of("maps/A.usmap").SameInputs(Of("maps/B.usmap")));
        Assert.False(Of("maps/A.usmap").SameInputs(Of(null)));
    }

    [Fact]
    public void WhetherTheMappingsLoaded_IsNotPartOfTheInputs()
    {
        Assert.True(Of("maps/A.usmap", loaded: true).SameInputs(Of("maps/A.usmap", loaded: false)));
        Assert.True(Of("maps/A.usmap", loaded: true).MappingsLoaded);
        Assert.False(Of("maps/A.usmap", loaded: false).MappingsLoaded);
    }

    [Fact]
    public void ADifferentVersionOrGame_IsADifferentInput()
    {
        Assert.False(Of(null).SameInputs(Of(null, version: EngineVersion.VER_UE5_4)));
        Assert.False(Of(null).SameInputs(Of(null, game: Game.MarvelRivals)));
    }
}
