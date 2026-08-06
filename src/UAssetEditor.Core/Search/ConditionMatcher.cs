using System.Text.RegularExpressions;

namespace UAssetEditor.Core.Search;

public static class ConditionMatcher
{
    /// <summary>An empty pattern list is treated as "no restriction" (always matches).</summary>
    public static bool Matches(string text, IReadOnlyList<string> patterns, MatchLogic logic, TextCompare compare)
    {
        if (patterns.Count == 0) return true;

        return logic == MatchLogic.And
            ? patterns.All(p => MatchesOne(text, p, compare))
            : patterns.Any(p => MatchesOne(text, p, compare));
    }

    private static bool MatchesOne(string text, string pattern, TextCompare compare) => compare switch
    {
        TextCompare.Equals => string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase),
        TextCompare.Contains => text.Contains(pattern, StringComparison.OrdinalIgnoreCase),
        TextCompare.Regex => Regex.IsMatch(text, pattern),
        _ => false,
    };
}
