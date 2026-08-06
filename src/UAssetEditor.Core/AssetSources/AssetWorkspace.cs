using System.Collections.Concurrent;
using UAssetAPI;
using UAssetEditor.Core.Concurrency;
using UAssetEditor.Core.Search;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Keeps a bounded set of assets open in memory across repeated searches/edits, for
/// interactive use: find some properties, edit several of them directly (across
/// different assets, in any order, without reselecting anything), then save the ones
/// that changed. This is deliberately separate from <see cref="Editing.EditExecutor"/>,
/// which opens/edits/saves/discards one asset at a time and is meant for large
/// rule-driven batches where keeping everything resident would be wasteful.
/// </summary>
public sealed class AssetWorkspace
{
    private readonly IAssetSource _source;
    private EngineVersionResolver _versions;
    private readonly SearchService _search = new();

    // Lazy<UAsset> rather than UAsset directly: ConcurrentDictionary.GetOrAdd can invoke
    // its factory more than once for the same key under concurrent access (only one
    // result is kept, but a losing call's side effects already happened) - wrapping in
    // Lazy means a second concurrent caller for the same new path may construct an extra
    // Lazy wrapper, but never actually opens the asset twice, since only the wrapper that
    // wins occupancy in the dictionary ever has .Value touched.
    private readonly ConcurrentDictionary<string, Lazy<UAsset>> _openAssets = new();

    public AssetWorkspace(IAssetSource source, EngineVersionResolver versions)
    {
        _source = source;
        _versions = versions;
    }

    public IReadOnlyCollection<string> OpenAssetPaths => _openAssets.Keys.ToList();

    public bool IsOpen(string assetPath) => _openAssets.ContainsKey(assetPath);

    /// <summary>Opens (and caches) an asset the first time it's requested; later calls reuse the same in-memory instance.</summary>
    public UAsset GetOrOpen(string assetPath) =>
        _openAssets.GetOrAdd(assetPath, path => new Lazy<UAsset>(() => _source.OpenAsset(path, _versions.Resolve(path), _versions.Mappings))).Value;

    public void Close(string assetPath) => _openAssets.TryRemove(assetPath, out _);

    public void CloseAll() => _openAssets.Clear();

    /// <summary>
    /// Swaps in a new engine-version/usmap resolver (e.g. the user changed the UE version
    /// or usmap in the UI) and drops every already-open asset, since each one was parsed
    /// under the old settings. The next <see cref="GetOrOpen"/> for a given path re-reads
    /// and re-parses it from the underlying source under the new settings - callers that
    /// want the currently-displayed content to reflect the change need to re-request it
    /// (e.g. re-run the last search) after calling this.
    /// </summary>
    public void UpdateVersionResolver(EngineVersionResolver versions)
    {
        _versions = versions;
        CloseAll();
    }

    /// <summary>
    /// Searches every asset in the underlying source in parallel, opening (or reusing an
    /// already-open) instance for each one. Assets that fail to open, or whose search
    /// throws (e.g. an invalid regex pattern in the query), are skipped.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        SearchQuery query,
        IProgress<SearchProgress>? progress = null,
        int? maxDegreeOfParallelism = null,
        CancellationToken cancellationToken = default)
    {
        var paths = _source.EnumerateAssetPaths().ToList();
        var results = new ConcurrentBag<SearchResult>();
        var completed = 0;

        await ThrottledParallel.ForEachAsync(paths, maxDegreeOfParallelism, (path, _) =>
        {
            try
            {
                var asset = GetOrOpen(path);
                foreach (var result in _search.SearchAsset(asset, path, query))
                    results.Add(result);
            }
            catch
            {
                // One bad asset/pattern shouldn't discard every other asset's results too.
            }

            var done = Interlocked.Increment(ref completed);
            progress?.Report(new SearchProgress(done, paths.Count, path));

            return Task.CompletedTask;
        }, cancellationToken);

        return results.ToList();
    }

    /// <summary>Saves only the requested (presumably dirty) already-open assets; paths that were never opened are ignored.</summary>
    public void SaveAll(IEnumerable<string> assetPaths, bool createBackup, string? backupFolder)
    {
        foreach (var path in assetPaths)
        {
            if (!_openAssets.TryGetValue(path, out var lazyAsset) || !lazyAsset.IsValueCreated) continue;
            _source.SaveAsset(lazyAsset.Value, path, createBackup, backupFolder);
        }
    }
}
