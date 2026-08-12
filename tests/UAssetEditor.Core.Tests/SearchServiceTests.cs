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

        var results = new SearchService().AllProperties(asset, "Fake/Path.uasset").ToList();

        var unsupported = Assert.Single(results, r => r.Kind == SearchMatchKind.Unsupported);
        Assert.Equal("BrokenExport", unsupported.ExportName);
        Assert.Equal(1, unsupported.ExportIndex);
        Assert.Null(unsupported.PropertyPath);
        Assert.Contains("4 raw byte", unsupported.MatchedText);

        Assert.Contains(results, r => r.Kind == SearchMatchKind.Property && r.PropertyPath == "Count");
    }

    [Fact]
    public void PropertiesForExport_ReturnsOnlyThatExportsProperties()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset, exportName: "First");
        TestAssets.CreateSampleExport(asset, exportName: "Second");

        var results = new SearchService().PropertiesForExport(asset, "Fake/Path.uasset", 1).ToList();

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

        var results = new SearchService().PropertiesUnder(asset, "Fake/Path.uasset", 0, "Location").ToList();
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
        var service = new SearchService();

        var results = service.AllProperties(asset, "Fake/Path.uasset").ToList();

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
        var service = new SearchService();

        var results = service.SearchAsset(asset, "Fake/Path.uasset", new SearchQuery { PropertyNamePatterns = ["Count"] }).ToList();

        var result = Assert.Single(results);
        Assert.Equal("Count", result.PropertyPath);
        Assert.Equal("5", result.MatchedText);
    }

    [Fact]
    public void SearchAsset_MatchesByValueAcrossNestedProperties()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);
        var service = new SearchService();

        var results = service.SearchAsset(asset, "Fake/Path.uasset", new SearchQuery { ValuePatterns = ["Alpha"] }).ToList();

        var result = Assert.Single(results);
        Assert.Equal("Tags[0]", result.PropertyPath);
    }

    [Fact]
    public void SearchAsset_CombinesNameAndValueFilters()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);
        var service = new SearchService();

        var noMatch = service.SearchAsset(asset, "p", new SearchQuery { PropertyNamePatterns = ["Count"], ValuePatterns = ["Alpha"] });

        Assert.Empty(noMatch);
    }

    [Fact]
    public void SearchAsset_ExportNamePatterns_RestrictsWhichExportsAreConsidered()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset, exportName: "Match_Me");
        TestAssets.CreateSampleExport(asset, exportName: "SkipThis");
        var service = new SearchService();

        var results = service.SearchAsset(asset, "p", new SearchQuery
        {
            ExportNamePatterns = ["Match_Me"],
            PropertyNamePatterns = ["Count"],
        }).ToList();

        Assert.Single(results);
        Assert.Equal("Match_Me", results[0].ExportName);
    }

    [Fact]
    public void SearchAsset_OrLogic_MatchesAnyPattern()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);
        var service = new SearchService();

        var results = service.SearchAsset(asset, "p", new SearchQuery
        {
            PropertyNamePatterns = ["Count", "DisplayName"],
            PropertyNameLogic = MatchLogic.Or,
        }).ToList();

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void SearchAsset_AndLogic_RequiresAllPatterns()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);
        var service = new SearchService();

        // No single property path contains both substrings, so AND should yield nothing,
        // while the same patterns under OR (default) would match "Location" and "Location.X"/"Location.Y".
        var andResults = service.SearchAsset(asset, "p", new SearchQuery
        {
            PropertyNamePatterns = ["Location", "DisplayName"],
            PropertyNameLogic = MatchLogic.And,
        }).ToList();

        Assert.Empty(andResults);
    }

    [Fact]
    public async Task SearchAllAsync_SkipsAssetsThatFailToOpenAndContinuesWithTheRest()
    {
        var goodAsset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(goodAsset);

        var source = new ThrowingThenGoodAssetSource("bad.uasset", "good.uasset", goodAsset);
        var versions = new EngineVersionResolver();
        var service = new SearchService();

        var results = await service.SearchAllAsync(source, versions, new SearchQuery { PropertyNamePatterns = ["Count"] });

        Assert.Single(results);
        Assert.Equal("good.uasset", results[0].AssetPath);
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
        var query = new SearchQuery { PropertyNamePatterns = ["["], PropertyNameCompare = TextCompare.Regex };

        var exception = await Record.ExceptionAsync(() => new SearchService().SearchAllAsync(source, new EngineVersionResolver(), query));

        Assert.Null(exception);
    }

    private sealed class ThrowingThenGoodAssetSource(string badPath, string goodPath, UAssetAPI.UAsset goodAsset) : Core.AssetSources.IAssetSource
    {
        public IEnumerable<string> EnumerateAssetPaths() => new[] { badPath, goodPath };

        public UAssetAPI.UAsset OpenAsset(string assetPath, UAssetAPI.UnrealTypes.EngineVersion engineVersion, UAssetAPI.Unversioned.Usmap? mappings) =>
            assetPath == badPath ? throw new InvalidOperationException("simulated parse failure") : goodAsset;

        public void SaveAsset(UAssetAPI.UAsset asset, string assetPath, bool createBackup, string? backupFolder) { }
    }
}
