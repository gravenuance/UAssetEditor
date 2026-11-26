using System.Text.RegularExpressions;

namespace UAssetEditor.Core.Search;

public static class ConditionMatcher
{
    /// <summary>
    /// An empty term list is "no restriction" (always matches). Otherwise: every AND term
    /// must match, at least one OR term must match if any OR terms are present, and no NOT
    /// term may match - NOT is a hard exclusion applied on top of whatever the AND/OR terms
    /// decide, not a third alternative combined the same way they are. A regex that runs past
    /// <paramref name="regexTimeout"/> (default: <see cref="SearchQuery.DefaultRegexTimeout"/>) throws <see cref="RegexMatchTimeoutException"/>.
    /// </summary>
    public static bool Matches(string text, IReadOnlyList<ConditionTerm> terms, TextCompare compare, TimeSpan? regexTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(terms);

        if (terms.Count == 0) return true;

        var sawOr = false;
        var anyOrMatched = false;

        foreach (var term in terms)
        {
            var isMatch = MatchesOne(text, term.Text, compare, regexTimeout);
            switch (term.Tag)
            {
                case TermTag.And when !isMatch:
                    return false;
                case TermTag.Or:
                    sawOr = true;
                    anyOrMatched |= isMatch;
                    break;
                case TermTag.Not when isMatch:
                    return false;
            }
        }

        return !sawOr || anyOrMatched;
    }

    private static bool MatchesOne(string text, string pattern, TextCompare compare, TimeSpan? regexTimeout) => compare switch
    {
        TextCompare.Equals => string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase),
        TextCompare.Contains => text.Contains(pattern, StringComparison.OrdinalIgnoreCase),
        TextCompare.Regex => RegexLimit.IsMatch(text, pattern, regexTimeout),
        _ => false,
    };
}
