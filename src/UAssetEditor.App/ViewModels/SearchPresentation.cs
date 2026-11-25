using UAssetEditor.Core.Search;

namespace UAssetEditor.App.ViewModels;

/// <summary>What the window searches with and says about a finished search, kept out of <see cref="MainViewModel"/> so tests can reach it.</summary>
public static class SearchPresentation
{
    /// <summary>The window's scope as a query; a regular expression that backtracks forever abandons its asset after the default limit instead of hanging the search.</summary>
    public static SearchQuery BuildQuery(
        IEnumerable<ConditionTermViewModel> exportNameTerms,
        IEnumerable<ConditionTermViewModel> propertyNameTerms,
        IEnumerable<ConditionTermViewModel> valueTerms,
        IEnumerable<ConditionTermViewModel> referenceTerms) => new()
    {
        ExportNameTerms = [.. exportNameTerms.Select(t => t.ToCore())],
        PropertyNameTerms = [.. propertyNameTerms.Select(t => t.ToCore())],
        ValueTerms = [.. valueTerms.Select(t => t.ToCore())],
        ReferenceTerms = [.. referenceTerms.Select(t => t.ToCore())],
        RegexTimeout = SearchQuery.DefaultRegexTimeout,
    };

    /// <summary>The lines shown above the results when some assets were not fully searched (the counts, then each named asset and why); empty when all were.</summary>
    public static string Notice(SearchOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return string.Join(Environment.NewLine, outcome.Warnings());
    }

    /// <summary>The status-bar line for a finished search; it says so when the matches may be incomplete.</summary>
    public static string Status(SearchOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var found = $"Found {outcome.Results.Count} match(es)";
        var unfinished = outcome.AssetsSkipped + outcome.RegexTimeouts;
        return unfinished == 0
            ? found + "."
            : $"{found}; {unfinished} of {outcome.AssetsSearched} asset(s) not fully searched.";
    }
}
