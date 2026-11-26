using System.Globalization;
using System.Text.RegularExpressions;

namespace UAssetEditor.Core.Search;

/// <summary>Runs user-supplied patterns under a per-match time limit (null takes the process default) and words the timeout for the user.</summary>
internal static class RegexLimit
{
    public static bool IsMatch(string input, string pattern, TimeSpan? limit) =>
        limit is { } timeout ? Regex.IsMatch(input, pattern, RegexOptions.None, timeout) : Regex.IsMatch(input, pattern);

    public static string Replace(string input, string pattern, string replacement, TimeSpan? limit) =>
        limit is { } timeout ? Regex.Replace(input, pattern, replacement, RegexOptions.None, timeout) : Regex.Replace(input, pattern, replacement);

    public static string Describe(RegexMatchTimeoutException timeout) =>
        string.Create(CultureInfo.InvariantCulture, $"regular expression timed out after {timeout.MatchTimeout.TotalSeconds:0.###} s");
}
