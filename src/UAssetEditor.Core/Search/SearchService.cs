using System.Text.RegularExpressions;
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
        var exportName = FNameDisplay.ToDisplayString(export.ObjectName);

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
        var exportName = FNameDisplay.ToDisplayString(normalExport.ObjectName);

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
                var exportName = FNameDisplay.ToDisplayString(export.ObjectName);

                if (!ConditionMatcher.Matches(exportName, query.ExportNameTerms, query.ExportNameCompare, query.RegexTimeout))
                    continue;

                foreach (var node in PropertyWalker.Walk(export))
                {
                    if (!ConditionMatcher.Matches(node.Path, query.PropertyNameTerms, query.PropertyNameCompare, query.RegexTimeout))
                        continue;

                    var text = PropertyValueAccessor.AsSearchableString(node.Property, asset);
                    if (query.ValueTerms.Count > 0 &&
                        (text == null || !ConditionMatcher.Matches(text, query.ValueTerms, query.ValueCompare, query.RegexTimeout)))
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
                if (ConditionMatcher.Matches(full, query.ReferenceTerms, query.ReferenceCompare, query.RegexTimeout))
                    yield return new SearchResult(assetPath, -1, "", SearchMatchKind.Reference, null, full);
            }
        }
    }

    private const int MaxSkipsKept = 20;

    /// <summary>
    /// Searches one opened asset and reports it as <see cref="SearchAllAsync"/> reports each of its assets: the first
    /// <paramref name="maxResults"/> hits in (export, property) order with every match counted, and a regular expression that
    /// runs past its time limit counted in the outcome, with the hits found before it, rather than thrown.
    /// </summary>
    public static SearchOutcome SearchOne(UAsset asset, string assetPath, SearchQuery query, int? maxResults = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(query);
        if (maxResults is < 0) throw new ArgumentOutOfRangeException(nameof(maxResults), maxResults, "The hit cap cannot be negative.");

        var found = new List<SearchResult>();
        IReadOnlyList<SearchSkip> skips = [];
        try
        {
            foreach (var result in SearchAsset(asset, assetPath, query)) found.Add(result);
        }
        catch (RegexMatchTimeoutException ex)
        {
            skips = [TimedOut(assetPath, ex)];
        }

        found.Sort(SearchResultOrder.Instance);
        return new SearchOutcome([.. found.Take(maxResults ?? int.MaxValue)], found.Count, 1, 0, skips.Count, skips);
    }

    private static SearchSkip TimedOut(string assetPath, RegexMatchTimeoutException ex) =>
        new(assetPath, $"regular expression timed out after {ex.MatchTimeout.TotalSeconds:0.###} s");

    /// <summary>
    /// Opens and searches every asset in <paramref name="source"/> in parallel. An asset that cannot be opened or searched
    /// (unsupported version, corrupt file, missing mappings, an invalid pattern) is skipped rather than aborting the batch,
    /// and one whose regular expression runs past its time limit is abandoned the same way; both are counted and the first
    /// few named in the outcome. Only the first <paramref name="maxResults"/> matches, in (asset, export, property) order,
    /// are kept (all of them when null), so a broad query over a huge folder cannot exhaust memory; the total still counts every match.
    /// </summary>
    public static async Task<SearchOutcome> SearchAllAsync(
        IAssetSource source,
        EngineVersionResolver versions,
        SearchQuery query,
        IProgress<SearchProgress>? progress = null,
        int? maxDegreeOfParallelism = null,
        int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(query);
        if (maxResults is < 0) throw new ArgumentOutOfRangeException(nameof(maxResults), maxResults, "The hit cap cannot be negative.");

        var paths = source.EnumerateAssetPaths().ToList();
        var hits = new FirstHits(maxResults);
        var skips = new FirstSkips(MaxSkipsKept);
        var completed = 0;
        var skipped = 0;
        var timeouts = 0;

        await ThrottledParallel.ForEachAsync(paths, maxDegreeOfParallelism, (path, _) =>
        {
            try
            {
                var asset = source.OpenAsset(path, versions.Resolve(path), versions.Mappings, versions.Game);
                foreach (var result in SearchAsset(asset, path, query))
                    hits.Add(result);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (RegexMatchTimeoutException ex)
            {
                Interlocked.Increment(ref timeouts);
                if (skips.WouldKeep(path)) skips.Add(TimedOut(path, ex));
            }
            catch (Exception ex)
            {
                // One bad asset must not discard every other asset's results; it is counted and named in the outcome, not lost.
                Interlocked.Increment(ref skipped);
                if (skips.WouldKeep(path)) skips.Add(new SearchSkip(path, $"{ex.GetType().Name}: {ex.Message}"));
            }

            var done = Interlocked.Increment(ref completed);
            progress?.Report(new SearchProgress(done, paths.Count, path));

            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);

        return new SearchOutcome(hits.Take(), hits.Total, paths.Count, skipped, timeouts, skips.Take());
    }

    /// <summary>
    /// Keeps the first <c>cap</c> skips by path while the search runs, so a folder of broken assets cannot pile up reasons.
    /// Which assets fail first depends on thread timing, which is why the named ones are the first by path.
    /// </summary>
    private sealed class FirstSkips(int cap)
    {
        private readonly List<SearchSkip> _kept = [];

        /// <summary>Whether a skip for this path would be kept now, so a caller can leave out building a reason that would be dropped.</summary>
        public bool WouldKeep(string assetPath)
        {
            lock (_kept) return Fits(assetPath);
        }

        public void Add(SearchSkip skip)
        {
            lock (_kept)
            {
                if (!Fits(skip.AssetPath)) return;
                _kept.Add(skip);
                _kept.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.AssetPath, b.AssetPath));
                if (_kept.Count > cap) _kept.RemoveAt(cap);
            }
        }

        public IReadOnlyList<SearchSkip> Take()
        {
            lock (_kept) return [.. _kept];
        }

        private bool Fits(string assetPath) =>
            _kept.Count < cap || StringComparer.OrdinalIgnoreCase.Compare(assetPath, _kept[^1].AssetPath) < 0;
    }

    /// <summary>Collects matches from many threads, keeping only the first <c>cap</c> in result order while counting them all.</summary>
    private sealed class FirstHits(int? cap)
    {
        private readonly List<SearchResult> _kept = [];
        private int _total;

        public int Total => Volatile.Read(ref _total);

        public void Add(SearchResult result)
        {
            Interlocked.Increment(ref _total);
            lock (_kept)
            {
                _kept.Add(result);
                // Trimmed in batches, so a hit costs a sort only now and then.
                if (cap is { } limit && _kept.Count >= Math.Max(2 * limit, 64)) Trim(limit);
            }
        }

        public IReadOnlyList<SearchResult> Take()
        {
            lock (_kept)
            {
                if (cap is { } limit) Trim(limit);
                else _kept.Sort(SearchResultOrder.Instance);
                return [.. _kept];
            }
        }

        private void Trim(int limit)
        {
            _kept.Sort(SearchResultOrder.Instance);
            if (_kept.Count > limit) _kept.RemoveRange(limit, _kept.Count - limit);
        }
    }
}
