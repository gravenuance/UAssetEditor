namespace UAssetEditor.Core.Search;

public enum TextCompare
{
    Contains,
    Equals,
    Regex,
}

public enum MatchLogic
{
    And,
    Or,
}

/// <summary>
/// Defines what to look for across a batch of assets. Every pattern list is a set of
/// conditions combined with its own <see cref="MatchLogic"/> (AND requires all to match,
/// OR requires any one to); an empty list means "no restriction" for that dimension.
/// At least one of <see cref="ExportNamePatterns"/>, <see cref="PropertyNamePatterns"/>,
/// or <see cref="ValuePatterns"/> must be non-empty for property search to run at all -
/// an entirely empty query intentionally matches nothing rather than silently selecting
/// every property in every asset (this doubles as a <c>RuleSet.Scope</c>, where an
/// accidental "match everything" would apply edits far more broadly than intended).
/// </summary>
public sealed class SearchQuery
{
    /// <summary>Restricts which exports ("entries") are considered at all, by their object name.</summary>
    public IReadOnlyList<string> ExportNamePatterns { get; init; } = Array.Empty<string>();
    public MatchLogic ExportNameLogic { get; init; } = MatchLogic.Or;
    public TextCompare ExportNameCompare { get; init; } = TextCompare.Contains;

    public IReadOnlyList<string> PropertyNamePatterns { get; init; } = Array.Empty<string>();
    public MatchLogic PropertyNameLogic { get; init; } = MatchLogic.Or;
    public TextCompare PropertyNameCompare { get; init; } = TextCompare.Contains;

    public IReadOnlyList<string> ValuePatterns { get; init; } = Array.Empty<string>();
    public MatchLogic ValueLogic { get; init; } = MatchLogic.Or;
    public TextCompare ValueCompare { get; init; } = TextCompare.Contains;

    /// <summary>Matches against the dotted import path (e.g. "T_Wall.T_Wall").</summary>
    public IReadOnlyList<string> ReferencePatterns { get; init; } = Array.Empty<string>();
    public MatchLogic ReferenceLogic { get; init; } = MatchLogic.Or;
    public TextCompare ReferenceCompare { get; init; } = TextCompare.Contains;

    internal bool HasPropertyCriteria =>
        ExportNamePatterns.Count > 0 || PropertyNamePatterns.Count > 0 || ValuePatterns.Count > 0;
}
