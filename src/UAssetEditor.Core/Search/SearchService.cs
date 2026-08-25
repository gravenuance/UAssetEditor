using System.Collections.Concurrent;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Concurrency;
using UAssetEditor.Core.PropertyAccess;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.Core.Search;

public static class SearchService
{
    /// <summary>
    /// Walks every export's property tree unconditionally - no <see cref="SearchQuery"/>
    /// filtering - for showing everything about one already-selected asset (e.g. opened
    /// from a browsable tree). An export that couldn't be parsed into properties (a
    /// <see cref="RawExport"/> - see <see cref="AssetSources.ResilientAssetLoader"/>) is
    /// surfaced as a single informational, non-editable row rather than silently omitted,
    /// so the export's existence stays visible even though its content isn't.
    /// </summary>
    public static IEnumerable<SearchResult> AllProperties(UAsset asset, string assetPath)
    {
        ArgumentNullException.ThrowIfNull(asset);

        for (var e = 0; e < asset.Exports.Count; e++)
            foreach (var result in PropertiesForExport(asset, assetPath, e))
                yield return result;
    }

    /// <summary>
    /// Same per-export logic as <see cref="AllProperties"/>, scoped to exactly one
    /// export - used when browsing the tree drills into a single export instead of
    /// wanting every export's properties at once (which, for an asset with many exports,
    /// can be tens of thousands of rows).
    /// </summary>
    public static IEnumerable<SearchResult> PropertiesForExport(UAsset asset, string assetPath, int exportIndex)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var export = asset.Exports[exportIndex];
        var exportName = export.ObjectName.Value?.Value ?? "";

        if (export is RawExport rawExport)
        {
            yield return new SearchResult(assetPath, exportIndex, exportName, SearchMatchKind.Unsupported, null,
                $"Could not be parsed into properties ({rawExport.Data.Length} raw byte(s)) - not editable.");
            yield break;
        }

        if (export is not NormalExport normalExport) yield break;

        foreach (var node in PropertyWalker.Walk(normalExport))
        {
            var text = PropertyValueAccessor.AsSearchableString(node.Property, asset);
            yield return new SearchResult(assetPath, exportIndex, exportName, SearchMatchKind.Property, node.Path, text ?? "");
        }
    }

    /// <summary>
    /// Same per-export logic as <see cref="PropertiesForExport"/>, scoped to just one
    /// property's own subtree - used when double-clicking a table reached by drilling into
    /// the Browse tree rather than a whole export. The tree itself never shows scalar leaf
    /// properties as their own entries (see <see cref="PropertyAccess.PropertyTreeExpander"/>),
    /// so this is how their values are actually reached: re-walk the export fresh (paths
    /// aren't cached) and keep only the node for the table itself plus everything under it.
    /// </summary>
    public static IEnumerable<SearchResult> PropertiesUnder(UAsset asset, string assetPath, int exportIndex, string propertyPath)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (asset.Exports[exportIndex] is not NormalExport normalExport) yield break;
        var exportName = normalExport.ObjectName.Value?.Value ?? "";

        foreach (var node in PropertyWalker.Walk(normalExport))
        {
            if (node.Path != propertyPath &&
                !node.Path.StartsWith(propertyPath + ".", StringComparison.Ordinal) &&
                !node.Path.StartsWith(propertyPath + "[", StringComparison.Ordinal))
                continue;

            var text = PropertyValueAccessor.AsSearchableString(node.Property, asset);
            yield return new SearchResult(assetPath, exportIndex, exportName, SearchMatchKind.Property, node.Path, text ?? "");
        }
    }

    /// <summary>Searches every export's property tree (and, if requested, the import table) of a single already-opened asset.</summary>
    public static IEnumerable<SearchResult> SearchAsset(UAsset asset, string assetPath, SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(query);

        if (query.HasPropertyCriteria)
        {
            for (var e = 0; e < asset.Exports.Count; e++)
            {
                if (asset.Exports[e] is not NormalExport export) continue;
                var exportName = export.ObjectName.Value?.Value ?? "";

                if (!ConditionMatcher.Matches(exportName, query.ExportNameTerms, query.ExportNameCompare))
                    continue;

                foreach (var node in PropertyWalker.Walk(export))
                {
                    if (!ConditionMatcher.Matches(node.Path, query.PropertyNameTerms, query.PropertyNameCompare))
                        continue;

                    var text = PropertyValueAccessor.AsSearchableString(node.Property, asset);
                    if (query.ValueTerms.Count > 0 &&
                        (text == null || !ConditionMatcher.Matches(text, query.ValueTerms, query.ValueCompare)))
                        continue;

                    yield return new SearchResult(assetPath, e, exportName, SearchMatchKind.Property, node.Path, text ?? "");
                }
            }
        }

        if (query.ReferenceTerms.Count > 0)
        {
            foreach (var import in asset.Imports)
            {
                var full = ImportPathResolver.GetFullPath(import, asset);
                if (ConditionMatcher.Matches(full, query.ReferenceTerms, query.ReferenceCompare))
                    yield return new SearchResult(assetPath, -1, "", SearchMatchKind.Reference, null, full);
            }
        }
    }

    /// <summary>
    /// Opens and searches every asset in <paramref name="source"/> in parallel. Assets
    /// that fail to open (unsupported version, corrupt file, missing mappings, etc.) are
    /// skipped rather than aborting the whole batch.
    /// </summary>
    public static async Task<IReadOnlyList<SearchResult>> SearchAllAsync(
        IAssetSource source,
        EngineVersionResolver versions,
        SearchQuery query,
        IProgress<SearchProgress>? progress = null,
        int? maxDegreeOfParallelism = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

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
        }, cancellationToken).ConfigureAwait(false);

        return results.ToList();
    }
}
