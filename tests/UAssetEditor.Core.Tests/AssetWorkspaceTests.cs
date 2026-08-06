using UAssetAPI;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Search;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.Core.Tests;

public class AssetWorkspaceTests
{
    [Fact]
    public void GetOrOpen_OpensOnceAndCachesTheSameInstance()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);
        var source = new CountingAssetSource(new Dictionary<string, UAsset> { ["a.uasset"] = asset });
        var workspace = new AssetWorkspace(source, new EngineVersionResolver());

        var first = workspace.GetOrOpen("a.uasset");
        var second = workspace.GetOrOpen("a.uasset");

        Assert.Same(first, second);
        Assert.Equal(1, source.OpenCount);
    }

    [Fact]
    public async Task GetOrOpen_ConcurrentCallsForANewPath_OnlyOpensItOnce()
    {
        // Regression test: ConcurrentDictionary.GetOrAdd can invoke its factory more than
        // once for the same key under concurrent access (only one result is kept, but a
        // losing call's side effects - here, actually opening/parsing the asset - already
        // happened). GetOrOpen wraps values in Lazy<UAsset> specifically to prevent this.
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);
        var source = new SlowCountingAssetSource(new Dictionary<string, UAsset> { ["a.uasset"] = asset });
        var workspace = new AssetWorkspace(source, new EngineVersionResolver());

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => workspace.GetOrOpen("a.uasset"))));

        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(1, source.OpenCount);
    }

    [Fact]
    public async Task SearchAsync_FindsMatchesAcrossCachedAssets()
    {
        var assetOne = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(assetOne);
        var assetTwo = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(assetTwo);

        var source = new CountingAssetSource(new Dictionary<string, UAsset>
        {
            ["a.uasset"] = assetOne,
            ["b.uasset"] = assetTwo,
        });
        var workspace = new AssetWorkspace(source, new EngineVersionResolver());

        var results = await workspace.SearchAsync(new SearchQuery { PropertyNamePatterns = ["Count"] });

        Assert.Equal(2, results.Count);
        Assert.True(workspace.IsOpen("a.uasset"));
        Assert.True(workspace.IsOpen("b.uasset"));
    }

    [Fact]
    public void SaveAll_OnlySavesRequestedPaths()
    {
        var assetOne = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(assetOne);
        var assetTwo = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(assetTwo);

        var source = new CountingAssetSource(new Dictionary<string, UAsset>
        {
            ["a.uasset"] = assetOne,
            ["b.uasset"] = assetTwo,
        });
        var workspace = new AssetWorkspace(source, new EngineVersionResolver());
        workspace.GetOrOpen("a.uasset");
        workspace.GetOrOpen("b.uasset");

        workspace.SaveAll(["a.uasset"], createBackup: false, backupFolder: null);

        Assert.Equal(["a.uasset"], source.SavedPaths);
    }

    [Fact]
    public void SaveAll_IgnoresPathsThatWereNeverOpened()
    {
        var asset = TestAssets.CreateAsset();
        TestAssets.CreateSampleExport(asset);
        var source = new CountingAssetSource(new Dictionary<string, UAsset> { ["a.uasset"] = asset });
        var workspace = new AssetWorkspace(source, new EngineVersionResolver());

        workspace.SaveAll(["a.uasset"], createBackup: false, backupFolder: null);

        Assert.Empty(source.SavedPaths);
    }

    private sealed class CountingAssetSource(Dictionary<string, UAsset> assets) : IAssetSource
    {
        public int OpenCount { get; private set; }
        public List<string> SavedPaths { get; } = new();

        public IEnumerable<string> EnumerateAssetPaths() => assets.Keys;

        public UAsset OpenAsset(string assetPath, UAssetAPI.UnrealTypes.EngineVersion engineVersion, UAssetAPI.Unversioned.Usmap? mappings)
        {
            OpenCount++;
            return assets[assetPath];
        }

        public void SaveAsset(UAsset asset, string assetPath, bool createBackup, string? backupFolder) => SavedPaths.Add(assetPath);
    }

    private sealed class SlowCountingAssetSource(Dictionary<string, UAsset> assets) : IAssetSource
    {
        private int _openCount;
        public int OpenCount => _openCount;

        public IEnumerable<string> EnumerateAssetPaths() => assets.Keys;

        public UAsset OpenAsset(string assetPath, UAssetAPI.UnrealTypes.EngineVersion engineVersion, UAssetAPI.Unversioned.Usmap? mappings)
        {
            Interlocked.Increment(ref _openCount);
            Thread.Sleep(20); // widen the race window so concurrent GetOrOpen calls are likely to actually overlap
            return assets[assetPath];
        }

        public void SaveAsset(UAsset asset, string assetPath, bool createBackup, string? backupFolder) { }
    }
}
