namespace UAssetEditor.Core.Search;

public enum SearchMatchKind
{
    Property,
    Reference,

    /// <summary>
    /// An export that exists but couldn't be parsed into properties (UAssetAPI fell back
    /// to <c>RawExport</c>) - informational only, never editable. Surfacing this is what
    /// keeps a partially-constructed asset's understood content visible/usable instead of
    /// the unparseable export just silently vanishing from view.
    /// </summary>
    Unsupported,
}

/// <summary>
/// A single match. For <see cref="SearchMatchKind.Property"/>, <see cref="ExportIndex"/>
/// and <see cref="PropertyPath"/> identify exactly where the match was found so it can be
/// re-located later by <c>EditExecutor</c>. For <see cref="SearchMatchKind.Reference"/>,
/// <see cref="ExportIndex"/> is -1 and <see cref="PropertyPath"/> is null since the match
/// is against the asset's import table, not a specific export's property. For
/// <see cref="SearchMatchKind.Unsupported"/>, <see cref="PropertyPath"/> is null.
/// </summary>
public sealed record SearchResult(
    string AssetPath,
    int ExportIndex,
    string ExportName,
    SearchMatchKind Kind,
    string? PropertyPath,
    string MatchedText);

public readonly record struct SearchProgress(int Completed, int Total, string CurrentAssetPath);
