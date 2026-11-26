using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetEditor.App.ViewModels;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Games;
using UAssetEditor.Core.Search;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.App.Tests;

public sealed class OpenContentRefreshTests
{
    // Opens "good.uasset" as one export holding Count = 5; any other path fails to open, and listing the paths can fail too.
    private sealed class Source(bool listingFails = false) : IAssetSource
    {
        public IEnumerable<string> EnumerateAssetPaths() =>
            listingFails ? throw new IOException("the pak is gone") : ["good.uasset", "bad.uasset"];

        public UAsset OpenAsset(string assetPath, EngineVersion engineVersion, Usmap? mappings, Game game)
        {
            if (assetPath != "good.uasset") throw new InvalidDataException("not a package");
            var asset = new UAsset(engineVersion);
            asset.ClearNameIndexList();
            asset.Exports = [];
            asset.Imports = [];
            asset.Exports.Add(new NormalExport(asset, [])
            {
                ObjectName = new FName(asset, "Export"),
                Data = [new IntPropertyData(new FName(asset, "Count")) { Value = 5 }],
            });
            return asset;
        }

        public void SaveAsset(UAsset asset, string assetPath, bool createBackup, string? backupFolder) { }
    }

    private static AssetWorkspace Workspace(bool listingFails = false) => new(new Source(listingFails), new EngineVersionResolver());

    [Fact]
    public async Task AnOpenedEntryThatCannotBeOpenedAgain_IsNamedInTheNotice_AndTheOthersStillShow()
    {
        OpenedScope[] opened = [new("bad.uasset", 0, null), new("good.uasset", 0, null)];

        var content = await OpenContentRefresh.RunAsync(Workspace(), null, opened, NullLogger.Instance);

        var row = Assert.Single(content.Results);
        Assert.Equal(("good.uasset", "Count", "5"), (row.AssetPath, row.PropertyPath, row.MatchedText));
        Assert.Equal("bad.uasset could not be shown again: not a package", content.Notice);
    }

    [Fact]
    public async Task ASearchThatCannotRunAgain_EmptiesTheGrid_AndSaysWhy()
    {
        var search = new SearchQuery { PropertyNameTerms = ["Count"] };

        var content = await OpenContentRefresh.RunAsync(Workspace(listingFails: true), search, [], NullLogger.Instance);

        Assert.Empty(content.Results);
        Assert.Equal("The search could not be run again: the pak is gone", content.Notice);
    }

    [Fact]
    public async Task ASearchRunsAgain_WithItsOwnNotice()
    {
        var search = new SearchQuery { PropertyNameTerms = ["Count"] };

        var content = await OpenContentRefresh.RunAsync(Workspace(), search, [new("good.uasset", 0, null)], NullLogger.Instance);

        Assert.Equal("good.uasset", Assert.Single(content.Results).AssetPath);
        Assert.StartsWith("1 of 2 asset(s) could not be searched", content.Notice, StringComparison.Ordinal);
    }
}
