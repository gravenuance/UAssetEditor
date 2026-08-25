using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Concurrency;
using UAssetEditor.Core.PropertyAccess;
using UAssetEditor.Core.Search;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.Core.Editing;

/// <summary>
/// Runs a <see cref="RuleSet"/> across an <see cref="IAssetSource"/>, processing assets in
/// parallel. <see cref="PreviewAsync"/> computes what would change without writing anything;
/// <see cref="ApplyAsync"/> does the same work and then saves each modified asset (optionally
/// after backing up the original file).
/// </summary>
public static class EditExecutor
{
    public static Task<IReadOnlyList<AssetChangeSet>> PreviewAsync(
        IAssetSource source,
        EngineVersionResolver versions,
        RuleSet ruleSet,
        IProgress<EditProgress>? progress = null,
        int? maxDegreeOfParallelism = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(versions);

        return RunAsync(source, path => source.OpenAsset(path, versions.Resolve(path), versions.Mappings), ruleSet, save: false, createBackup: false, backupFolder: null, progress, maxDegreeOfParallelism, cancellationToken);
    }

    public static Task<IReadOnlyList<AssetChangeSet>> ApplyAsync(
        IAssetSource source,
        EngineVersionResolver versions,
        RuleSet ruleSet,
        bool createBackup,
        string? backupFolder,
        IProgress<EditProgress>? progress = null,
        int? maxDegreeOfParallelism = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(versions);

        return RunAsync(source, path => source.OpenAsset(path, versions.Resolve(path), versions.Mappings), ruleSet, save: true, createBackup, backupFolder, progress, maxDegreeOfParallelism, cancellationToken);
    }

    /// <summary>
    /// Computes and applies rule matches against whatever <paramref name="openAsset"/> returns
    /// - e.g. an <see cref="AssetSources.AssetWorkspace"/>'s GetOrOpen - so a touched asset stays
    /// resident and mutated in memory (same as a manual grid-cell edit) instead of being parsed
    /// fresh and thrown away. Never saves: unlike <see cref="ApplyAsync"/>, the caller decides
    /// if/when the changes actually get written, exactly like it already does for manual edits.
    /// </summary>
    public static Task<IReadOnlyList<AssetChangeSet>> StageAsync(
        IAssetSource source,
        Func<string, UAsset> openAsset,
        RuleSet ruleSet,
        IProgress<EditProgress>? progress = null,
        int? maxDegreeOfParallelism = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        return RunAsync(source, openAsset, ruleSet, save: false, createBackup: false, backupFolder: null, progress, maxDegreeOfParallelism, cancellationToken);
    }

    private static async Task<IReadOnlyList<AssetChangeSet>> RunAsync(
        IAssetSource source,
        Func<string, UAsset> openAsset,
        RuleSet ruleSet,
        bool save,
        bool createBackup,
        string? backupFolder,
        IProgress<EditProgress>? progress,
        int? maxDegreeOfParallelism,
        CancellationToken cancellationToken)
    {
        var paths = source.EnumerateAssetPaths().ToList();
        var results = new ConcurrentBag<AssetChangeSet>();
        var completed = 0;

        await ThrottledParallel.ForEachAsync(paths, maxDegreeOfParallelism, (path, _) =>
        {
            var changeSet = ProcessAsset(source, openAsset, ruleSet, path, save, createBackup, backupFolder);
            if (changeSet != null)
                results.Add(changeSet);

            var done = Interlocked.Increment(ref completed);
            progress?.Report(new EditProgress(done, paths.Count, path));

            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);

        return results.ToList();
    }

    private static AssetChangeSet? ProcessAsset(
        IAssetSource source,
        Func<string, UAsset> openAsset,
        RuleSet ruleSet,
        string path,
        bool save,
        bool createBackup,
        string? backupFolder)
    {
        UAsset asset;
        try
        {
            asset = openAsset(path);
        }
        catch
        {
            // Skip assets UAssetAPI can't parse with the resolved engine version/mappings.
            return null;
        }

        try
        {
            var changes = new List<PropertyChange>();
            var propertyMatches = SearchService.SearchAsset(asset, path, ruleSet.Scope)
                .Where(r => r.Kind == SearchMatchKind.Property)
                .ToList();

            // Built lazily, one PropertyWalker pass per export instead of one per
            // (rule, match) pair - re-locating a property from scratch for every rule
            // applied to every match made this loop cost O(rules * matches * treeSize)
            // on large exports. Only invalidated when a rule actually restructures the
            // export (removes a property, or adds/removes an array element), since that's
            // the only time a cached node's Owner/OwnerIndex can go stale.
            var nodeCache = new Dictionary<int, Dictionary<string, PropertyNode>>();

            foreach (var rule in ruleSet.Rules)
            {
                if (rule is ReplaceReferenceRule referenceRule)
                {
                    changes.AddRange(ApplyReferenceRule(asset, path, referenceRule));
                    continue;
                }

                foreach (var match in propertyMatches)
                {
                    if (match.PropertyPath == null) continue;

                    var node = LocateCached(asset, nodeCache, match.ExportIndex, match.PropertyPath);
                    if (node == null) continue;

                    var change = ApplyPropertyRule(asset, path, match, rule, node);
                    if (change == null) continue;

                    changes.Add(change);
                    if (IsStructural(rule))
                        nodeCache.Remove(match.ExportIndex);
                }
            }

            if (changes.Count == 0) return null;

            if (save)
                source.SaveAsset(asset, path, createBackup, backupFolder);

            return new AssetChangeSet(path, changes);
        }
        catch
        {
            // One asset's rule application shouldn't discard results already computed
            // for every other asset in the batch - e.g. an invalid regex pattern in a
            // rule/scope, or a property shape UAssetAPI didn't expect. Skip it and keep
            // going rather than letting the whole run's results be lost.
            return null;
        }
    }

    private static bool IsStructural(EditRule rule) => rule is RemovePropertyRule or AddTagRule or RemoveTagRule;

    private static PropertyNode? LocateCached(UAsset asset, Dictionary<int, Dictionary<string, PropertyNode>> cache, int exportIndex, string propertyPath)
    {
        if (!cache.TryGetValue(exportIndex, out var byPath))
        {
            byPath = BuildNodeIndex(asset, exportIndex);
            cache[exportIndex] = byPath;
        }

        return byPath.TryGetValue(propertyPath, out var node) ? node : null;
    }

    private static Dictionary<string, PropertyNode> BuildNodeIndex(UAsset asset, int exportIndex)
    {
        var byPath = new Dictionary<string, PropertyNode>();
        if (exportIndex < 0 || exportIndex >= asset.Exports.Count) return byPath;
        if (asset.Exports[exportIndex] is not NormalExport export) return byPath;

        foreach (var node in PropertyWalker.Walk(export))
            byPath[node.Path] = node;

        return byPath;
    }

    private static PropertyChange? ApplyPropertyRule(UAsset asset, string path, SearchResult match, EditRule rule, PropertyNode node)
    {
        var oldValue = PropertyValueAccessor.AsSearchableString(node.Property, asset) ?? "";

        switch (rule)
        {
            case SetPropertyValueRule setRule:
                if (setRule.Skip != null && ShouldSkip(node.Property, setRule.Skip, oldValue))
                    return null;
                if (!PropertyValueAccessor.TrySetStringValue(node.Property, setRule.NewValue, asset))
                    return null;
                break;

            case NumericAdjustRule numericRule:
                {
                    if (!TryGetNumericValue(node.Property, out var currentNumeric, out var isInteger))
                        return null;

                    if (numericRule.Skip != null && SkipEvaluator.ShouldSkipNumeric(numericRule.Skip, currentNumeric))
                        return null;

                    if (!double.TryParse(numericRule.TargetValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var targetNumeric))
                        return null;

                    if (!TryApplyNumericOperation(numericRule.Operation, currentNumeric, targetNumeric, out var newNumeric))
                        return null; // e.g. divide by zero - skip rather than corrupt the value

                    var newValueText = isInteger
                        ? ((long)Math.Round(newNumeric)).ToString(CultureInfo.InvariantCulture)
                        : newNumeric.ToString(CultureInfo.InvariantCulture);

                    if (!PropertyValueAccessor.TrySetStringValue(node.Property, newValueText, asset))
                        return null;
                    break;
                }

            case ReplaceTextRule textRule:
                if (oldValue.Length == 0) return null;
                var replaced = textRule.IsRegex
                    ? Regex.Replace(oldValue, textRule.Pattern, textRule.Replacement)
                    : oldValue.Replace(textRule.Pattern, textRule.Replacement, StringComparison.Ordinal);
                if (replaced == oldValue) return null;
                if (!PropertyValueAccessor.TrySetStringValue(node.Property, replaced, asset))
                    return null;
                break;

            case RemovePropertyRule:
                if (node.Owner is not List<PropertyData> ownerList) return null;
                ownerList.RemoveAt(node.OwnerIndex);
                return new PropertyChange(path, match.ExportIndex, match.ExportName, match.PropertyPath, "RemoveProperty", oldValue, "");

            case AddTagRule addTag:
                if (node.Property is not ArrayPropertyData addToArray) return null;
                var appended = (addToArray.Value ?? Array.Empty<PropertyData>())
                    .Append((PropertyData)new NamePropertyData(addToArray.Name) { Value = new FName(asset, addTag.Tag) })
                    .ToArray();
                addToArray.Value = appended;
                return new PropertyChange(path, match.ExportIndex, match.ExportName, match.PropertyPath, "AddTag", oldValue, addTag.Tag);

            case RemoveTagRule removeTag:
                if (node.Property is not ArrayPropertyData removeFromArray || removeFromArray.Value == null) return null;
                var remaining = removeFromArray.Value
                    .Where(element => PropertyValueAccessor.AsSearchableString(element, asset) != removeTag.Tag)
                    .ToArray();
                if (remaining.Length == removeFromArray.Value.Length) return null;
                removeFromArray.Value = remaining;
                return new PropertyChange(path, match.ExportIndex, match.ExportName, match.PropertyPath, "RemoveTag", oldValue, removeTag.Tag);

            default:
                return null;
        }

        PropertyValueAccessor.UpdateIsZeroFlag(node.Property);
        var newValue = PropertyValueAccessor.AsSearchableString(node.Property, asset) ?? "";
        return new PropertyChange(path, match.ExportIndex, match.ExportName, match.PropertyPath, rule.GetType().Name, oldValue, newValue);
    }

    private static bool ShouldSkip(PropertyData prop, SkipCondition skip, string currentValueText) =>
        TryGetNumericValue(prop, out var numeric, out _)
            ? SkipEvaluator.ShouldSkipNumeric(skip, numeric)
            : SkipEvaluator.ShouldSkipText(skip, currentValueText);

    private static bool TryGetNumericValue(PropertyData prop, out double value, out bool isInteger)
    {
        switch (prop)
        {
            case IntPropertyData ip: value = ip.Value; isInteger = true; return true;
            case Int64PropertyData i64: value = i64.Value; isInteger = true; return true;
            case FloatPropertyData fp: value = fp.Value; isInteger = false; return true;
            case DoublePropertyData dp: value = dp.Value; isInteger = false; return true;
            default: value = 0; isInteger = false; return false;
        }
    }

    private static bool TryApplyNumericOperation(string operation, double current, double target, out double result)
    {
        // CA1308 prefers ToUpperInvariant, but the "set"/"add"/"sub"/"mul"/"div" operation
        // keywords are already lowercase everywhere else they appear (MainViewModel's
        // NumericOperations list, RuleSet JSON, tests) - normalizing to lowercase here is
        // what actually matches them, not a casualness that needs fixing.
#pragma warning disable CA1308
        switch (operation.Trim().ToLowerInvariant())
#pragma warning restore CA1308
        {
            case "set": result = target; return true;
            case "add": result = current + target; return true;
            case "sub": result = current - target; return true;
            case "mul": result = current * target; return true;
            case "div":
                if (Math.Abs(target) < 0.000001) { result = current; return false; }
                result = current / target;
                return true;
            default:
                result = current;
                return false;
        }
    }

    private static IEnumerable<PropertyChange> ApplyReferenceRule(UAsset asset, string path, ReplaceReferenceRule rule)
    {
        foreach (var import in asset.Imports)
        {
            var full = ImportPathResolver.GetFullPath(import, asset);
            var isMatch = rule.IsRegex ? Regex.IsMatch(full, rule.OldReference) : full == rule.OldReference;
            if (!isMatch) continue;

            var newFull = rule.IsRegex ? Regex.Replace(full, rule.OldReference, rule.NewReference) : rule.NewReference;
            var dot = newFull.LastIndexOf('.');
            var newLeaf = dot >= 0 ? newFull[(dot + 1)..] : newFull;

            var oldName = import.ObjectName.Value?.Value ?? "";
            if (newLeaf == oldName) continue;

            import.ObjectName = new FName(asset, newLeaf);
            yield return new PropertyChange(path, -1, "", null, "ReplaceReference", oldName, newLeaf);
        }
    }
}
