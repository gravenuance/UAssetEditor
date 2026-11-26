using System.IO;
using Microsoft.Extensions.Logging;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Search;

namespace UAssetEditor.App.ViewModels;

/// <summary>What the results grid shows after a refresh, and the notice above it ("" for none).</summary>
public sealed record RefreshedContent(IReadOnlyList<SearchResult> Results, string Notice);

/// <summary>
/// Re-runs whichever of the last search or the last opened tree entries filled the results grid, against a (just-reloaded)
/// workspace. A failure ends up in the notice, never thrown: the reload, revert or apply that asked for the refresh has
/// already happened, so its caller must not report it as failed.
/// </summary>
public static class OpenContentRefresh
{
    public static async Task<RefreshedContent> RunAsync(AssetWorkspace workspace, SearchQuery? lastSearch, IReadOnlyList<OpenedScope> lastOpened, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(lastOpened);
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

        var results = new List<SearchResult>();
        var failures = new List<string>();
        foreach (var scope in lastOpened)
        {
            try
            {
                results.AddRange(await Task.Run(() =>
                {
                    var asset = workspace.GetOrOpen(scope.AssetPath);
                    return scope.PropertyPath == null
                        ? SearchService.PropertiesForExport(asset, scope.AssetPath, scope.ExportIndex).ToList()
                        : SearchService.PropertiesUnder(asset, scope.AssetPath, scope.ExportIndex, scope.PropertyPath).ToList();
                }));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Reopening '{AssetPath}' for the results grid failed.", scope.AssetPath);
                failures.Add($"{Path.GetFileName(scope.AssetPath)} could not be shown again: {ex.Message}");
            }
        }

        return new RefreshedContent(results, string.Join(Environment.NewLine, failures));
    }
}
