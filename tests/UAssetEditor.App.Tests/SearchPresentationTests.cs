using UAssetEditor.App.ViewModels;
using UAssetEditor.Core.Search;

namespace UAssetEditor.App.Tests;

public class SearchPresentationTests
{
    private static SearchResult Hit(string asset) => new(asset, 0, "Export", SearchMatchKind.Property, "Count", "5");

    [Fact]
    public void BuildQuery_GivesTheSearchTheDefaultRegexLimit_SoABackTrackingPatternCannotHangIt()
    {
        var query = SearchPresentation.BuildQuery([], [], [], []);

        Assert.Equal(TimeSpan.FromSeconds(3), query.RegexTimeout);
    }

    [Fact]
    public void BuildQuery_CarriesEveryTermWithItsTag()
    {
        var query = SearchPresentation.BuildQuery(
            [new ConditionTermViewModel("Door")],
            [new ConditionTermViewModel("Count", TermTag.Or)],
            [new ConditionTermViewModel("7", TermTag.Not)],
            [new ConditionTermViewModel("T_Wall")]);

        Assert.Equal([new ConditionTerm("Door")], query.ExportNameTerms);
        Assert.Equal([new ConditionTerm("Count", TermTag.Or)], query.PropertyNameTerms);
        Assert.Equal([new ConditionTerm("7", TermTag.Not)], query.ValueTerms);
        Assert.Equal([new ConditionTerm("T_Wall")], query.ReferenceTerms);
    }

    [Fact]
    public void Notice_IsEmpty_WhenEveryAssetWasSearched()
    {
        var outcome = new SearchOutcome([Hit("a.uasset")], 1, 2, 0, 0, []);

        Assert.Equal("", SearchPresentation.Notice(outcome));
        Assert.Equal("Found 1 match(es).", SearchPresentation.Status(outcome));
    }

    [Fact]
    public void Notice_SaysWhichAssetsHitTheRegexLimit_AndThatTheirMatchesMayBeIncomplete()
    {
        var outcome = new SearchOutcome([Hit("a.uasset")], 1, 2, 0, 1, [new SearchSkip("b.uasset", "regular expression timed out after 3 s")]);

        Assert.Equal(
            "1 of 2 asset(s) hit the regular-expression time limit; their matches may be incomplete." + Environment.NewLine +
            "b.uasset: regular expression timed out after 3 s",
            SearchPresentation.Notice(outcome));
        Assert.Equal("Found 1 match(es); 1 of 2 asset(s) not fully searched.", SearchPresentation.Status(outcome));
    }

    [Fact]
    public void Notice_SaysWhichAssetsCouldNotBeSearched_AndWhy()
    {
        var outcome = new SearchOutcome([], 0, 3, 2, 0,
        [
            new SearchSkip("x.uasset", "InvalidOperationException: bad header"),
            new SearchSkip("y.uasset", "IOException: locked"),
        ]);

        Assert.Equal(
            "2 of 3 asset(s) could not be searched." + Environment.NewLine +
            "x.uasset: InvalidOperationException: bad header" + Environment.NewLine +
            "y.uasset: IOException: locked",
            SearchPresentation.Notice(outcome));
        Assert.Equal("Found 0 match(es); 2 of 3 asset(s) not fully searched.", SearchPresentation.Status(outcome));
    }

    [Fact]
    public void Notice_SaysHowManyMoreAssetsThanTheOnesNamed_WereNotSearched()
    {
        var outcome = new SearchOutcome([], 0, 500, 498, 2, [new SearchSkip("x.uasset", "IOException: locked"), new SearchSkip("y.uasset", "IOException: locked")]);

        var lines = SearchPresentation.Notice(outcome).Split(Environment.NewLine);

        Assert.Equal(
            ["498 of 500 asset(s) could not be searched.", "2 of 500 asset(s) hit the regular-expression time limit; their matches may be incomplete.",
             "x.uasset: IOException: locked", "y.uasset: IOException: locked", "...and 498 more not listed."],
            lines);
    }

    [Fact]
    public void Status_CountsBothKindsOfTrouble()
    {
        var outcome = new SearchOutcome([], 0, 10, 2, 3, []);

        Assert.Equal("Found 0 match(es); 5 of 10 asset(s) not fully searched.", SearchPresentation.Status(outcome));
    }
}
