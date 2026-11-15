using UAssetEditor.Cli;
using UAssetEditor.Core.Search;

namespace UAssetEditor.Core.Tests;

/// <summary>What <c>uacli search</c> prints after its matches: the match count, and what could not be searched.</summary>
public class CliSearchReportTests
{
    private static SearchResult Hit(string asset) => new(asset, 0, "E", SearchMatchKind.Property, "P", "v");

    [Fact]
    public void Summary_WhenEveryAssetWasSearched_IsTheMatchCountAlone()
    {
        var outcome = new SearchOutcome([Hit("a.uasset"), Hit("b.uasset")], 2, 5, 0, 0, []);

        Assert.Equal(["-- 2 match(es)"], Commands.SearchSummary(outcome));
        Assert.Empty(Commands.SearchWarnings(outcome));
    }

    [Fact]
    public void Summary_CountsEveryMatch_NotOnlyTheOnesKept()
    {
        var outcome = new SearchOutcome([Hit("a.uasset")], 7, 5, 0, 0, []);

        Assert.Equal(["-- 7 match(es)"], Commands.SearchSummary(outcome));
    }

    [Fact]
    public void Summary_SaysHowManyAssetsWereNotSearched_AndWarningsNameThem()
    {
        var outcome = new SearchOutcome(
            [], 0, 5, 2, 1,
            [new SearchSkip("bad.uasset", "InvalidOperationException: cannot open"), new SearchSkip("slow.uasset", "regular expression timed out after 3 s")]);

        Assert.Equal(
            ["-- 0 match(es)", "-- not searched: 2 of 5 asset(s) could not be read or searched, 1 hit the regular-expression time limit"],
            Commands.SearchSummary(outcome));
        Assert.Equal(
            ["Warning: bad.uasset: InvalidOperationException: cannot open", "Warning: slow.uasset: regular expression timed out after 3 s"],
            Commands.SearchWarnings(outcome));
    }
}
