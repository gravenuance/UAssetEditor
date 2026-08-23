namespace UAssetEditor.Core.Search;

public enum TextCompare
{
    Contains,
    Equals,
    Regex,
}

/// <summary>
/// How one <see cref="ConditionTerm"/> combines with the other terms in the same field -
/// see <see cref="ConditionMatcher.Matches"/> for the exact combination rule.
/// </summary>
public enum TermTag
{
    And,
    Or,
    Not,
}

/// <summary>
/// One filter value plus how it combines with its siblings. The implicit string conversion
/// lets a plain string collection expression (<c>["Count"]</c>) still work anywhere a single,
/// untagged AND term is enough - Tag only matters once more than one term is in play.
/// </summary>
public sealed record ConditionTerm(string Text, TermTag Tag = TermTag.And)
{
    public static implicit operator ConditionTerm(string text) => new(text);
}

/// <summary>
/// Defines what to look for across a batch of assets. Every term list is a set of
/// <see cref="ConditionTerm"/>s, each individually tagged AND/OR/NOT and combined per
/// <see cref="ConditionMatcher.Matches"/>; an empty list means "no restriction" for that
/// dimension. At least one of <see cref="ExportNameTerms"/>, <see cref="PropertyNameTerms"/>,
/// or <see cref="ValueTerms"/> must be non-empty for property search to run at all -
/// an entirely empty query intentionally matches nothing rather than silently selecting
/// every property in every asset (this doubles as a <c>RuleSet.Scope</c>, where an
/// accidental "match everything" would apply edits far more broadly than intended).
/// </summary>
public sealed class SearchQuery
{
    /// <summary>Restricts which exports ("entries") are considered at all, by their object name.</summary>
    public IReadOnlyList<ConditionTerm> ExportNameTerms { get; init; } = Array.Empty<ConditionTerm>();
    public TextCompare ExportNameCompare { get; init; } = TextCompare.Contains;

    public IReadOnlyList<ConditionTerm> PropertyNameTerms { get; init; } = Array.Empty<ConditionTerm>();
    public TextCompare PropertyNameCompare { get; init; } = TextCompare.Contains;

    public IReadOnlyList<ConditionTerm> ValueTerms { get; init; } = Array.Empty<ConditionTerm>();
    public TextCompare ValueCompare { get; init; } = TextCompare.Contains;

    /// <summary>Matches against the dotted import path (e.g. "T_Wall.T_Wall").</summary>
    public IReadOnlyList<ConditionTerm> ReferenceTerms { get; init; } = Array.Empty<ConditionTerm>();
    public TextCompare ReferenceCompare { get; init; } = TextCompare.Contains;

    internal bool HasPropertyCriteria =>
        ExportNameTerms.Count > 0 || PropertyNameTerms.Count > 0 || ValueTerms.Count > 0;
}
