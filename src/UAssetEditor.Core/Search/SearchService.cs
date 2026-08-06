using System.Collections.Concurrent;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Concurrency;
using UAssetEditor.Core.PropertyAccess;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.Core.Search;

public sealed class SearchService
{
    /// <summary>Walks every export's property tree unconditionally - no <see cref="SearchQuery"/> filtering - for showing everything about one already-selected asset (e.g. opened from a browsable tree).</summary>
    public IEnumerable<SearchResult> AllProperties(UAsset asset, string assetPath)
    {
        for (var e = 0; e < asset.Exports.Count; e++)
        {
            if (asset.Exports[e] is not NormalExport export) continue;
            var exportName = export.ObjectName.Value?.Value ?? "";

            foreach (var node in PropertyWalker.Walk(export))
            {
                var text = PropertyValueAccessor.AsSearchableString(node.Property, asset);
                yield return new SearchResult(assetPath, e, exportName, SearchMatchKind.Property, node.Path, text ?? "");
            }
        }
    }

    /// <summary>Searches every export's property tree (and, if requested, the import table) of a single already-opened asset.</summary>
    public IEnumerable<SearchResult> SearchAsset(UAsset asset, string assetPath, SearchQuery query)
    {
        if (query.HasPropertyCriteria)
        {
            for (var e = 0; e < asset.Exports.Count; e++)
            {
                if (asset.Exports[e] is not NormalExport export) continue;
                var exportName = export.ObjectName.Value?.Value ?? "";

                if (!ConditionMatcher.Matches(exportName, query.ExportNamePatterns, query.ExportNameLogic, query.ExportNameCompare))
                    continue;

                foreach (var node in PropertyWalker.Walk(export))
                {
                    if (!ConditionMatcher.Matches(node.Path, query.PropertyNamePatterns, query.PropertyNameLogic, query.PropertyNameCompare))
                        continue;

                    var text = PropertyValueAccessor.AsSearchableString(node.Property, asset);
                    if (query.ValuePatterns.Count > 0 &&
                        (text == null || !ConditionMatcher.Matches(text, query.ValuePatterns, query.ValueLogic, query.ValueCompare)))
                        continue;

                    yield return new SearchResult(assetPath, e, exportName, SearchMatchKind.Property, node.Path, text ?? "");
                }
            }
        }

        if (query.ReferencePatterns.Count > 0)
        {
            foreach (var import in asset.Imports)
            {
                var full = ImportPathResolver.GetFullPath(import, asset);
                if (ConditionMatcher.Matches(full, query.ReferencePatterns, query.ReferenceLogic, query.ReferenceCompare))
                    yield return new SearchResult(assetPath, -1, "", SearchMatchKind.Reference, null, full);
            }
        }
    }

    /// <summary>
    /// Opens and searches every asset in <paramref name="source"/> in parallel. Assets
    /// that fail to open (unsupported version, corrupt file, missing mappings, etc.) are
    /// skipped rather than aborting the whole batch.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAllAsync(
        IAssetSource source,
        EngineVersionResolver versions,
        SearchQuery query,
        IProgress<SearchProgress>? progress = null,
        int? maxDegreeOfParallelism = null,
        CancellationToken cancellationToken = default)
    {
        var paths = source.EnumerateAssetPaths().ToList();
        var results = new ConcurrentBag<SearchResult>();
        var completed = 0;

        await ThrottledParallel.ForEachAsync(paths, maxDegreeOfParallelism, (path, _) =>
        {
            try
            {
                var asset = source.OpenAsset(path, versions.Resolve(path), versions.Mappings);
                foreach (var result in SearchAsset(asset, path, query))
                    results.Add(result);
            }
            catch
            {
                // Skip assets that fail to open, or whose search throws (e.g. an invalid
                // regex pattern in the query) - one bad asset shouldn't discard every
                // other asset's results too.
            }

            var done = Interlocked.Increment(ref completed);
            progress?.Report(new SearchProgress(done, paths.Count, path));

            return Task.CompletedTask;
        }, cancellationToken);

        return results.ToList();
    }
}
