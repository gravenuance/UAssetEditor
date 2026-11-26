using System.Globalization;
using System.Text.RegularExpressions;

namespace UAssetEditor.Core.Search;

/// <summary>Runs user-supplied patterns under a per-match time limit (null takes <see cref="SearchQuery.DefaultRegexTimeout"/>) and words the timeout for the user.</summary>
internal static class RegexLimit
{
    public static bool IsMatch(string input, string pattern, TimeSpan? limit) =>
        Regex.IsMatch(input, pattern, RegexOptions.None, limit ?? SearchQuery.DefaultRegexTimeout);

    public static string Replace(string input, string pattern, string replacement, TimeSpan? limit) =>
        Regex.Replace(input, pattern, replacement, RegexOptions.None, limit ?? SearchQuery.DefaultRegexTimeout);

    // The range Regex itself accepts: positive and at most int.MaxValue milliseconds, or no limit at all.
    public static bool IsValid(TimeSpan limit) =>
        limit == Regex.InfiniteMatchTimeout || (limit > TimeSpan.Zero && limit.TotalMilliseconds <= int.MaxValue);

    public static string Describe(RegexMatchTimeoutException timeout) =>
        string.Create(CultureInfo.InvariantCulture, $"regular expression timed out after {timeout.MatchTimeout.TotalSeconds:0.###} s");
}
