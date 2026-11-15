namespace UAssetEditor.Core.Search;

/// <summary>An asset a batch search could not search, and why.</summary>
public sealed record SearchSkip(string AssetPath, string Reason);

/// <summary>
/// What a batch search found and what it could not look at. <see cref="Results"/> holds at most the hit cap given to the
/// search, the first ones in (asset, export, property) order; <see cref="TotalMatches"/> counts every match all the same.
/// </summary>
/// <param name="AssetsSearched">Every asset the source listed, whether or not it could be searched.</param>
/// <param name="AssetsSkipped">Assets that failed to open or whose search failed (an invalid pattern, a corrupt export); not those in <paramref name="RegexTimeouts"/>.</param>
/// <param name="RegexTimeouts">Assets whose search a regular expression held up past its time limit; the matches found before that are kept.</param>
/// <param name="Skips">The first few of each, with the reason, so the cause can be seen without a log.</param>
public sealed record SearchOutcome(
    IReadOnlyList<SearchResult> Results,
    int TotalMatches,
    int AssetsSearched,
    int AssetsSkipped,
    int RegexTimeouts,
    IReadOnlyList<SearchSkip> Skips)
{
    /// <summary>One line per kind of trouble, or none when every asset was searched; for a terminal or a tool result to show.</summary>
    public IReadOnlyList<string> Warnings()
    {
        var lines = new List<string>();
        if (AssetsSkipped > 0) lines.Add($"{AssetsSkipped} of {AssetsSearched} asset(s) could not be searched.");
        if (RegexTimeouts > 0) lines.Add($"{RegexTimeouts} of {AssetsSearched} asset(s) hit the regular-expression time limit; their matches may be incomplete.");
        lines.AddRange(Skips.Select(skip => $"{skip.AssetPath}: {skip.Reason}"));
        return lines;
    }
}
