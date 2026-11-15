namespace UAssetEditor.Core.Search;

/// <summary>The order search results are listed in: by asset path (ignoring case), then export, then property path.</summary>
public sealed class SearchResultOrder : IComparer<SearchResult>
{
    public static SearchResultOrder Instance { get; } = new();

    private SearchResultOrder()
    {
    }

    public int Compare(SearchResult? x, SearchResult? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var byAsset = StringComparer.OrdinalIgnoreCase.Compare(x.AssetPath, y.AssetPath);
        if (byAsset != 0) return byAsset;
        var byExport = x.ExportIndex.CompareTo(y.ExportIndex);
        if (byExport != 0) return byExport;
        var byPath = StringComparer.Ordinal.Compare(x.PropertyPath, y.PropertyPath);
        if (byPath != 0) return byPath;
        var byKind = x.Kind.CompareTo(y.Kind);
        return byKind != 0 ? byKind : StringComparer.Ordinal.Compare(x.MatchedText, y.MatchedText);
    }
}
