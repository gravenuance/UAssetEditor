using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.Search;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.Core.Tests;

public class SearchServiceTests
{
    [Fact]
    public void AllProperties_SurfacesRawExportsAsUnsupportedButStillReturnsNormalExportProperties()
    {
        // Regression test for "retain what is properly constructed": an asset with one
        // export that couldn't be parsed into properties (UAssetAPI's RawExport fallback)
        // shouldn't lose visibility into that export, nor should it prevent properties on
        // the asset's other, successfully-parsed exports from still showing up.
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset, exportName: "GoodExport");
        var rawExport = new RawExport([1, 2, 3, 4], asset, [])
        {
            ObjectName = new FName(asset, "BrokenExport"),
        };
        asset.Exports.Add(rawExport);

        var results = SearchService.AllProperties(asset, "Fake/Path.uasset").ToList();

        var unsupported = Assert.Single(results, r => r.Kind == SearchMatchKind.Unsupported);
        Assert.Equal("BrokenExport", unsupported.ExportName);
        Assert.Equal(1, unsupported.ExportIndex);
        Assert.Null(unsupported.PropertyPath);
        Assert.Contains("4 raw byte", unsupported.MatchedText, StringComparison.Ordinal);

        Assert.Contains(results, r => r.Kind == SearchMatchKind.Property && r.PropertyPath == "Count");
    }

    [Fact]
    public void PropertiesForExport_ReturnsOnlyThatExportsProperties()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset, exportName: "First");
        TestAssets.CreateSampleExport(asset, exportName: "Second");

        var results = SearchService.PropertiesForExport(asset, "Fake/Path.uasset", 1).ToList();

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal("Second", r.ExportName));
        Assert.All(results, r => Assert.Equal(1, r.ExportIndex));
    }

    [Fact]
    public void PropertiesUnder_ReturnsOnlyTheGivenTableAndItsOwnDescendants()
    {
        // This is how double-clicking a table reached by drilling into the Browse tree
        // opens it into the edit grid - the tree itself never shows scalar leaf
        // properties (Location's own X/Y) as their own entries, so this scoped walk is
        // the only way their values are actually reached.
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);

        var results = SearchService.PropertiesUnder(asset, "Fake/Path.uasset", 0, "Location").ToList();
        var paths = results.Select(r => r.PropertyPath).ToList();

        Assert.Contains("Location", paths);
        Assert.Contains("Location.X", paths);
        Assert.Contains("Location.Y", paths);
        Assert.DoesNotContain("bEnabled", paths);
        Assert.DoesNotContain("Count", paths);
        Assert.DoesNotContain("Tags", paths);

        Assert.Equal("1.5", results.Single(r => r.PropertyPath == "Location.X").MatchedText);
    }

    [Fact]
    public void AllProperties_ReturnsEveryPropertyRegardlessOfAnyQuery()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);

        var results = SearchService.AllProperties(asset, "Fake/Path.uasset").ToList();

        var paths = results.Select(r => r.PropertyPath).ToList();
        Assert.Contains("bEnabled", paths);
        Assert.Contains("Count", paths);
        Assert.Contains("DisplayName", paths);
        Assert.Contains("Location.X", paths);
        Assert.Contains("Tags[0]", paths);
        Assert.All(results, r => Assert.Equal(SearchMatchKind.Property, r.Kind));
    }

    [Fact]
    public void SearchAsset_MatchesByPropertyName()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);

        var results = SearchService.SearchAsset(asset, "Fake/Path.uasset", new SearchQuery { PropertyNameTerms = ["Count"] }).ToList();

        var result = Assert.Single(results);
        Assert.Equal("Count", result.PropertyPath);
        Assert.Equal("5", result.MatchedText);
    }

    [Fact]
    public void SearchAsset_MatchesByValueAcrossNestedProperties()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);

        var results = SearchService.SearchAsset(asset, "Fake/Path.uasset", new SearchQuery { ValueTerms = ["Alpha"] }).ToList();

        var result = Assert.Single(results);
        Assert.Equal("Tags[0]", result.PropertyPath);
    }

    [Fact]
    public void SearchAsset_CombinesNameAndValueFilters()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);

        var noMatch = SearchService.SearchAsset(asset, "p", new SearchQuery { PropertyNameTerms = ["Count"], ValueTerms = ["Alpha"] });

        Assert.Empty(noMatch);
    }

    [Fact]
    public void SearchAsset_ShowsAndMatchesNumberedExportNames()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset, exportName: "Task");
        TestAssets.CreateSampleExport(asset, exportName: "Task").ObjectName = new FName(asset, "Task", 4);

        var results = SearchService.SearchAsset(asset, "p", new SearchQuery
        {
            ExportNameTerms = ["Task_3"],
            PropertyNameTerms = ["Count"],
        }).ToList();

        Assert.Single(results);
        Assert.Equal("Task_3", results[0].ExportName);
    }

    [Fact]
    public void PropertiesForExport_ShowsTheNumberedExportName()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset, exportName: "Task").ObjectName = new FName(asset, "Task", 2);

        Assert.All(SearchService.PropertiesForExport(asset, "p", 0), r => Assert.Equal("Task_1", r.ExportName));
    }

    [Fact]
    public void SearchAsset_ExportNameTerms_RestrictsWhichExportsAreConsidered()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset, exportName: "Match_Me");
        TestAssets.CreateSampleExport(asset, exportName: "SkipThis");

        var results = SearchService.SearchAsset(asset, "p", new SearchQuery
        {
            ExportNameTerms = ["Match_Me"],
            PropertyNameTerms = ["Count"],
        }).ToList();

        Assert.Single(results);
        Assert.Equal("Match_Me", results[0].ExportName);
    }

    [Fact]
    public void SearchAsset_OrTags_MatchAnyPattern()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);

        var results = SearchService.SearchAsset(asset, "p", new SearchQuery
        {
            PropertyNameTerms = [new ConditionTerm("Count", TermTag.Or), new ConditionTerm("DisplayName", TermTag.Or)],
        }).ToList();

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void SearchAsset_AndTags_RequireAllPatterns()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);

        // No single property path contains both substrings, so AND should yield nothing,
        // while the same patterns tagged Or would match "Location" and "Location.X"/"Location.Y".
        var andResults = SearchService.SearchAsset(asset, "p", new SearchQuery
        {
            PropertyNameTerms = [new ConditionTerm("Location", TermTag.And), new ConditionTerm("DisplayName", TermTag.And)],
        }).ToList();

        Assert.Empty(andResults);
    }

    [Fact]
    public void SearchAsset_NotTag_ExcludesMatchesEvenWhenOtherTermsMatch()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);

        // "Location" alone would match "Location", "Location.X", "Location.Y" - the Not
        // term for "Location.X" should exclude just that one, regardless of the Or match.
        var results = SearchService.SearchAsset(asset, "p", new SearchQuery
        {
            PropertyNameTerms = [new ConditionTerm("Location", TermTag.Or), new ConditionTerm("Location.X", TermTag.Not)],
        }).ToList();

        Assert.DoesNotContain(results, r => r.PropertyPath == "Location.X");
        Assert.Contains(results, r => r.PropertyPath == "Location");
        Assert.Contains(results, r => r.PropertyPath == "Location.Y");
    }

    [Fact]
    public async Task SearchAllAsync_SkipsAssetsThatFailToOpenAndContinuesWithTheRest()
    {
        var goodAsset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(goodAsset);

        var source = new ThrowingThenGoodAssetSource("bad.uasset", "good.uasset", goodAsset);
        var versions = new EngineVersionResolver();

        var outcome = await SearchService.SearchAllAsync(source, versions, new SearchQuery { PropertyNameTerms = ["Count"] }, cancellationToken: TestContext.Current.CancellationToken);

        var result = Assert.Single(outcome.Results);
        Assert.Equal("good.uasset", result.AssetPath);
    }

    [Fact]
    public async Task SearchAllAsync_DoesNotThrow_WhenPatternIsInvalidRegex()
    {
        // Regression test: a query pattern with TextCompare.Regex used to throw during
        // SearchAsset for every asset, and that exception was never caught (only OpenAsset
        // failures were), aborting SearchAllAsync entirely instead of just yielding nothing.
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);
        var source = new InMemoryAssetSource(new Dictionary<string, UAssetAPI.UAsset> { ["a.uasset"] = asset });
        var query = new SearchQuery { PropertyNameTerms = ["["], PropertyNameCompare = TextCompare.Regex };

        var exception = await Record.ExceptionAsync(() => SearchService.SearchAllAsync(source, new EngineVersionResolver(), query, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    private static InMemoryAssetSource ThreeAssets(string exportName = "TestExport")
    {
        var assets = new Dictionary<string, UAssetAPI.UAsset>();
        foreach (var name in new[] { "c.uasset", "a.uasset", "b.uasset" })
        {
            var asset = TestAssets.CreateAsset();
            TestAssets.CreateSampleExport(asset, exportName);
            assets[name] = asset;
        }
        return new InMemoryAssetSource(assets);
    }

    [Fact]
    public async Task SearchAllAsync_StopsCollectingAtTheHitCap_ButCountsEveryMatch()
    {
        var query = new SearchQuery { PropertyNameTerms = ["Count"] }; // one match per asset

        var outcome = await SearchService.SearchAllAsync(ThreeAssets(), new EngineVersionResolver(), query, maxResults: 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["a.uasset", "b.uasset"], outcome.Results.Select(r => r.AssetPath));
        Assert.Equal(3, outcome.TotalMatches);
        Assert.Equal(3, outcome.AssetsSearched);
        Assert.Equal(0, outcome.AssetsSkipped);
        Assert.Empty(outcome.Warnings());
    }

    [Fact]
    public async Task SearchAllAsync_WithoutACap_KeepsEveryMatch_InAssetOrder()
    {
        var query = new SearchQuery { PropertyNameTerms = ["Count"] };

        var outcome = await SearchService.SearchAllAsync(ThreeAssets(), new EngineVersionResolver(), query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["a.uasset", "b.uasset", "c.uasset"], outcome.Results.Select(r => r.AssetPath));
        Assert.Equal(3, outcome.TotalMatches);
    }

    [Fact]
    public async Task SearchAllAsync_ReportsTheAssetsItCouldNotSearch_AndWhy()
    {
        var goodAsset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(goodAsset);
        var source = new ThrowingThenGoodAssetSource("bad.uasset", "good.uasset", goodAsset);

        var outcome = await SearchService.SearchAllAsync(source, new EngineVersionResolver(), new SearchQuery { PropertyNameTerms = ["Count"] }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.AssetsSkipped);
        Assert.Equal(2, outcome.AssetsSearched);
        Assert.Equal(0, outcome.RegexTimeouts);
        var skip = Assert.Single(outcome.Skips);
        Assert.Equal("bad.uasset", skip.AssetPath);
        Assert.Contains("simulated parse failure", skip.Reason, StringComparison.Ordinal);
        Assert.Contains("1 of 2 asset(s) could not be searched.", outcome.Warnings());
        Assert.Contains("bad.uasset: InvalidOperationException: simulated parse failure", outcome.Warnings());
    }

    [Fact]
    public async Task SearchAllAsync_WithAnInvalidRegex_CountsEveryAssetAsSkipped()
    {
        var query = new SearchQuery { PropertyNameTerms = ["["], PropertyNameCompare = TextCompare.Regex };

        var outcome = await SearchService.SearchAllAsync(ThreeAssets(), new EngineVersionResolver(), query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, outcome.AssetsSkipped);
        Assert.Equal(0, outcome.TotalMatches);
    }

    [Fact]
    public async Task SearchAllAsync_ReportsTheAssetsWhoseRegexTimedOut_SeparatelyFromThoseItCouldNotOpen()
    {
        // 40 letters then a letter that fails the pattern: the nested quantifier backtracks for far longer than the limit.
        var query = new SearchQuery
        {
            ExportNameTerms = ["^(a+)+$"],
            ExportNameCompare = TextCompare.Regex,
            RegexTimeout = TimeSpan.FromMilliseconds(1),
        };
        var source = ThreeAssets(new string('a', 40) + "b");

        var outcome = await SearchService.SearchAllAsync(source, new EngineVersionResolver(), query, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, outcome.RegexTimeouts);
        Assert.Equal(0, outcome.AssetsSkipped);
        Assert.Equal(0, outcome.TotalMatches);
        Assert.Contains("3 of 3 asset(s) hit the regular-expression time limit; their matches may be incomplete.", outcome.Warnings());
    }

    [Fact]
    public void SearchOne_CountsARegexThatRunsOutOfTime_InsteadOfThrowing()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset, new string('a', 40) + "b");
        var query = new SearchQuery { ExportNameTerms = ["^(a+)+$"], ExportNameCompare = TextCompare.Regex, RegexTimeout = TimeSpan.FromMilliseconds(1) };

        var outcome = SearchService.SearchOne(asset, "x.uasset", query);

        Assert.Equal(1, outcome.AssetsSearched);
        Assert.Equal(1, outcome.RegexTimeouts);
        Assert.Equal(0, outcome.AssetsSkipped);
        Assert.Contains("1 of 1 asset(s) hit the regular-expression time limit; their matches may be incomplete.", outcome.Warnings());
    }

    [Fact]
    public void SearchOne_KeepsTheFirstHitsUpToTheCap_ButCountsEveryMatch()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset, "First");
        TestAssets.CreateSampleExport(asset, "Second");
        var query = new SearchQuery { PropertyNameTerms = ["Count"] }; // one Count property per export

        var outcome = SearchService.SearchOne(asset, "x.uasset", query, maxResults: 1);

        Assert.Equal(2, outcome.TotalMatches);
        Assert.Equal("First", Assert.Single(outcome.Results).ExportName);
    }

    [Fact]
    public async Task SearchAllAsync_KeepsOnlyTheFirstFewSkipReasons_ButCountsAll()
    {
        var outcome = await SearchService.SearchAllAsync(new ManyBadAssetsSource(100), new EngineVersionResolver(), new SearchQuery { PropertyNameTerms = ["Count"] }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(100, outcome.AssetsSkipped);
        Assert.Equal(20, outcome.Skips.Count);
    }

    [Fact]
    public async Task SearchAllAsync_DoesNotBuildSkipReasonsItWillNotKeep()
    {
        var messageReads = 0;
        var source = new FailingAssetsSource(200, descending: false, () => new MessageCountingException(() => Interlocked.Increment(ref messageReads)));

        var outcome = await SearchService.SearchAllAsync(source, new EngineVersionResolver(), new SearchQuery { PropertyNameTerms = ["Count"] }, maxDegreeOfParallelism: 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(200, outcome.AssetsSkipped);
        Assert.Equal(20, outcome.Skips.Count);
        Assert.Equal(20, messageReads);
    }

    [Fact]
    public async Task SearchAllAsync_NamesTheFirstSkippedPathsWhateverOrderTheAssetsFail()
    {
        var source = new FailingAssetsSource(200, descending: true, () => new InvalidOperationException("cannot open"));

        var outcome = await SearchService.SearchAllAsync(source, new EngineVersionResolver(), new SearchQuery { PropertyNameTerms = ["Count"] }, maxDegreeOfParallelism: 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(200, outcome.AssetsSkipped);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"bad{i:D3}.uasset"), outcome.Skips.Select(skip => skip.AssetPath));
    }

#pragma warning disable CA1032 // A test double built with one delegate; the standard exception constructors would be unused.
    private sealed class MessageCountingException(Action onMessageRead) : InvalidOperationException
#pragma warning restore CA1032
    {
        public override string Message
        {
            get
            {
                onMessageRead();
                return "cannot open";
            }
        }
    }

    private sealed class FailingAssetsSource(int count, bool descending, Func<Exception> failure) : Core.AssetSources.IAssetSource
    {
        public IEnumerable<string> EnumerateAssetPaths()
        {
            var indexes = Enumerable.Range(0, count);
            return (descending ? indexes.Reverse() : indexes).Select(i => $"bad{i:D3}.uasset");
        }

        public UAssetAPI.UAsset OpenAsset(string assetPath, UAssetAPI.UnrealTypes.EngineVersion engineVersion, UAssetAPI.Unversioned.Usmap? mappings, UAssetEditor.Core.Games.Game game) =>
            throw failure();

        public void SaveAsset(UAssetAPI.UAsset asset, string assetPath, bool createBackup, string? backupFolder) { }
    }

    private sealed class ManyBadAssetsSource(int count) : Core.AssetSources.IAssetSource
    {
        public IEnumerable<string> EnumerateAssetPaths() => Enumerable.Range(0, count).Select(i => $"bad{i}.uasset");

        public UAssetAPI.UAsset OpenAsset(string assetPath, UAssetAPI.UnrealTypes.EngineVersion engineVersion, UAssetAPI.Unversioned.Usmap? mappings, UAssetEditor.Core.Games.Game game) =>
            throw new InvalidOperationException("cannot open");

        public void SaveAsset(UAssetAPI.UAsset asset, string assetPath, bool createBackup, string? backupFolder) { }
    }

    private sealed class ThrowingThenGoodAssetSource(string badPath, string goodPath, UAssetAPI.UAsset goodAsset) : Core.AssetSources.IAssetSource
    {
        public IEnumerable<string> EnumerateAssetPaths() => new[] { badPath, goodPath };

        public UAssetAPI.UAsset OpenAsset(string assetPath, UAssetAPI.UnrealTypes.EngineVersion engineVersion, UAssetAPI.Unversioned.Usmap? mappings, UAssetEditor.Core.Games.Game game) =>
            assetPath == badPath ? throw new InvalidOperationException("simulated parse failure") : goodAsset;

        public void SaveAsset(UAssetAPI.UAsset asset, string assetPath, bool createBackup, string? backupFolder) { }
    }
}
