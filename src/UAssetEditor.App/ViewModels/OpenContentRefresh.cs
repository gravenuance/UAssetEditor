using System.IO;
using Microsoft.Extensions.Logging;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Search;

namespace UAssetEditor.App.ViewModels;

/// <summary>An asset whose opened entries could not be shown, and why.</summary>
public sealed record ScopeFailure(string AssetPath, string Reason);

/// <summary>The rows for a set of opened tree entries, the assets that could not be shown, and their open warnings.</summary>
public sealed record LoadedScopes(IReadOnlyList<SearchResult> Results, IReadOnlyList<ScopeFailure> Failures, IReadOnlyList<string> Warnings);

/// <summary>What the results grid shows after a refresh, and the notice above it ("" for none).</summary>
public sealed record RefreshedContent(IReadOnlyList<SearchResult> Results, string Notice);

/// <summary>Fills the results grid from opened tree entries or the last search, kept out of <see cref="MainViewModel"/> so tests can reach it.</summary>
public static class OpenContentRefresh
{
    /// <summary>
    /// Opens each asset once, off the UI thread, and lists the rows of its entries. An asset that cannot be opened or listed
    /// is a <see cref="ScopeFailure"/>, not an exception, so the other assets' rows still show.
    /// </summary>
    public static async Task<LoadedScopes> LoadAsync(AssetWorkspace workspace, IReadOnlyList<OpenedScope> scopes, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);

        var results = new List<SearchResult>();
        var failures = new List<ScopeFailure>();
        var warnings = new List<string>();
        foreach (var asset in scopes.GroupBy(scope => scope.AssetPath, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var (rows, opened) = await Task.Run(() =>
                {
                    var uasset = workspace.GetOrOpen(asset.Key);
                    var listed = asset.SelectMany(scope => scope.PropertyPath == null
                        ? SearchService.PropertiesForExport(uasset, scope.AssetPath, scope.ExportIndex)
                        : SearchService.PropertiesUnder(uasset, scope.AssetPath, scope.ExportIndex, scope.PropertyPath)).ToList();
                    return (listed, ResilientAssetLoader.WarningsFor(uasset));
                });
                results.AddRange(rows);
                warnings.AddRange(opened);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Showing the opened entries of '{AssetPath}' failed.", asset.Key);
                failures.Add(new ScopeFailure(asset.Key, ex.Message));
            }
        }

        return new LoadedScopes(results, failures, warnings);
    }

    /// <summary>
    /// Re-runs whichever of the last search or the last opened tree entries filled the results grid, against a (just-reloaded)
    /// workspace. A failure ends up in the notice, not thrown: the reload, revert or apply that asked for the refresh has
    /// already happened, so its caller must not report it as failed. Only cancellation is thrown.
    /// </summary>
    public static async Task<RefreshedContent> RunAsync(AssetWorkspace workspace, SearchQuery? lastSearch, IReadOnlyList<OpenedScope> lastOpened, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(logger);

        if (lastSearch != null)
        {
            try
            {
                var outcome = await workspace.SearchAsync(lastSearch, maxDegreeOfParallelism: 0);
                return new RefreshedContent(outcome.Results, SearchPresentation.Notice(outcome));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Re-running the last search after a reload failed.");
                return new RefreshedContent([], $"The search could not be run again: {ex.Message}");
            }
        }

        var loaded = await LoadAsync(workspace, lastOpened, logger);
        var lines = loaded.Failures.Select(failure => $"{Path.GetFileName(failure.AssetPath)} could not be shown again: {failure.Reason}")
            .Concat(loaded.Warnings.Distinct(StringComparer.Ordinal));
        return new RefreshedContent(loaded.Results, string.Join(Environment.NewLine, lines));
    }
}
