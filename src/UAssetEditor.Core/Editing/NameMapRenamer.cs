using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.Editing;

/// <summary>One whole-entry name-map replacement, e.g. a package path or an object name.</summary>
public readonly record struct NameRename(string From, string To)
{
    /// <summary>Parses "from=to", splitting on the first '='; both sides must be non-empty.</summary>
    public static NameRename Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var split = text.IndexOf('=', StringComparison.Ordinal);
        if (split <= 0 || split == text.Length - 1)
            throw new FormatException($"'{text}' is not a from=to pair.");
        return new NameRename(text[..split], text[(split + 1)..]);
    }
}

/// <summary>Whether a rename's source existed in the name map.</summary>
public readonly record struct NameRenameResult(NameRename Rename, bool Found);

/// <summary>
/// Rewrites whole name-map entries. Every FName, import and package path in a package points into
/// the name map by index, so replacing entries retargets them all at once: this is how a cooked
/// asset is cloned to a new path, or made to reference different packages, without re-cooking it.
/// </summary>
public static class NameMapRenamer
{
    /// <summary>
    /// Applies every rename at once (so swaps work) and also updates the package's own folder name
    /// when it matches. Refuses, changing nothing, if a target would duplicate an existing entry.
    /// </summary>
    public static IReadOnlyList<NameRenameResult> Rename(UAsset asset, IReadOnlyList<NameRename> renames)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(renames);

        var bySource = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rename in renames)
        {
            if (string.IsNullOrWhiteSpace(rename.To)) throw new ArgumentException($"Rename of '{rename.From}' has an empty target.", nameof(renames));
            if (!bySource.TryAdd(rename.From, rename.To)) throw new ArgumentException($"'{rename.From}' is renamed more than once.", nameof(renames));
        }

        var names = asset.GetNameMapIndexList().Select(n => n.Value).ToList();
        var renamed = names.Select(n => bySource.GetValueOrDefault(n, n)).ToList();
        var duplicate = renamed.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) throw new ArgumentException($"Renaming would leave '{duplicate.Key}' in the name map twice.", nameof(renames));

        var encodings = asset.GetNameMapIndexList().Select(n => n.Encoding).ToList();
        asset.ClearNameIndexList();
        for (var i = 0; i < renamed.Count; i++)
            asset.AddNameReference(new FString(renamed[i], encodings[i]), forceAddDuplicates: true, skipFixes: true);

        if (asset.FolderName?.Value is { } folder && bySource.TryGetValue(folder, out var newFolder))
            asset.FolderName = new FString(newFolder, asset.FolderName.Encoding);

        var present = names.ToHashSet(StringComparer.Ordinal);
        return [.. renames.Select(r => new NameRenameResult(r, present.Contains(r.From)))];
    }
}
