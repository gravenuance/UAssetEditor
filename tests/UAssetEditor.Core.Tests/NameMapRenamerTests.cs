using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.Editing;

namespace UAssetEditor.Core.Tests;

public class NameMapRenamerTests
{
    private static UAssetAPI.UAsset AssetWithNames(params string[] names)
    {
        var asset = TestAssets.CreateAsset();
        foreach (var name in names) asset.AddNameReference(new FString(name));
        return asset;
    }

    [Fact]
    public void Rename_ReplacesTheWholeEntry_AndReportsOldToNew()
    {
        var asset = AssetWithNames("/Game/A/MI_Old", "MI_Old", "Keep");

        var results = NameMapRenamer.Rename(asset, [new NameRename("/Game/A/MI_Old", "/Game/B/MI_New"), new NameRename("MI_Old", "MI_New")]);

        Assert.Equal(["/Game/B/MI_New", "MI_New", "Keep"], asset.GetNameMapIndexList().Select(n => n.Value));
        Assert.All(results, r => Assert.True(r.Found));
    }

    [Fact]
    public void Rename_KeepsReferencesPointingAtTheSameIndex()
    {
        var asset = AssetWithNames("T_Old");
        var reference = new FName(asset, "T_Old");

        NameMapRenamer.Rename(asset, [new NameRename("T_Old", "T_New")]);

        Assert.Equal("T_New", reference.Value.Value);
        Assert.Equal(0, asset.SearchNameReference(new FString("T_New")));
    }

    [Fact]
    public void Rename_MatchesWholeEntriesOnly_NotSubstrings()
    {
        var asset = AssetWithNames("MI_Old_Lobby");

        var results = NameMapRenamer.Rename(asset, [new NameRename("MI_Old", "MI_New")]);

        Assert.Equal("MI_Old_Lobby", asset.GetNameMapIndexList()[0].Value);
        Assert.False(results.Single().Found);
    }

    [Fact]
    public void Rename_UpdatesThePackageFolderName_WhenItMatches()
    {
        var asset = AssetWithNames("/Game/A/MI_Old");
        asset.FolderName = new FString("/Game/A/MI_Old");

        NameMapRenamer.Rename(asset, [new NameRename("/Game/A/MI_Old", "/Game/B/MI_New")]);

        Assert.Equal("/Game/B/MI_New", asset.FolderName.Value);
    }

    [Fact]
    public void Rename_RefusesATargetThatAlreadyExists_AndChangesNothing()
    {
        var asset = AssetWithNames("A", "B", "C");

        Assert.Throws<ArgumentException>(() => NameMapRenamer.Rename(asset, [new NameRename("C", "D"), new NameRename("A", "B")]));

        Assert.Equal(["A", "B", "C"], asset.GetNameMapIndexList().Select(n => n.Value));
    }

    [Fact]
    public void Rename_RefusesTwoRenamesOfTheSameSource()
    {
        var asset = AssetWithNames("A");

        Assert.Throws<ArgumentException>(() => NameMapRenamer.Rename(asset, [new NameRename("A", "B"), new NameRename("A", "C")]));
    }

    [Fact]
    public void Rename_AllowsSwappingNamesInOnePass()
    {
        var asset = AssetWithNames("A", "B");

        NameMapRenamer.Rename(asset, [new NameRename("A", "B"), new NameRename("B", "A")]);

        Assert.Equal(["B", "A"], asset.GetNameMapIndexList().Select(n => n.Value));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Rename_RefusesAnEmptyTarget(string target)
    {
        var asset = AssetWithNames("A");

        Assert.Throws<ArgumentException>(() => NameMapRenamer.Rename(asset, [new NameRename("A", target)]));
    }

    [Theory]
    [InlineData("a=b", "a", "b")]
    [InlineData("/Game/X/T_1=/Game/Y/T_2", "/Game/X/T_1", "/Game/Y/T_2")]
    public void Parse_SplitsOnTheFirstEqualsSign(string text, string from, string to)
    {
        Assert.Equal(new NameRename(from, to), NameRename.Parse(text));
    }

    [Theory]
    [InlineData("noequals")]
    [InlineData("=b")]
    [InlineData("a=")]
    public void Parse_RejectsMalformedPairs(string text)
    {
        Assert.Throws<FormatException>(() => NameRename.Parse(text));
    }
}
