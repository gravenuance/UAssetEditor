using System.IO;
using UAssetEditor.Core.Editing;

namespace UAssetEditor.App.ViewModels;

/// <summary>What the window says about a finished rule-set preview or apply, kept out of <see cref="MainViewModel"/> so tests can reach it.</summary>
public static class EditPresentation
{
    /// <summary>
    /// The status text's tail naming failed and skipped assets; empty when none. With <paramref name="editsKept"/> (an apply,
    /// whose edits stay in the open assets) it also says when a failed asset keeps the edits made before its failure.
    /// </summary>
    public static string Failures(IReadOnlyList<AssetChangeSet> changeSets, bool editsKept)
    {
        ArgumentNullException.ThrowIfNull(changeSets);

        var skippedCount = changeSets.Count(c => c.Skipped);
        var skipped = skippedCount == 0 ? "" : $" {skippedCount} unreadable asset(s) skipped.";
        var failed = changeSets.Where(c => c.Failed).ToList();
        if (failed.Count == 0) return skipped;

        var partial = editsKept && failed.Any(c => c.Changes.Count > 0)
            ? " Edits made before a failure are kept unsaved; Revert drops them."
            : "";
        return $" {failed.Count} asset(s) failed, first {Path.GetFileName(failed[0].AssetPath)}: {failed[0].Error}.{partial}{skipped}";
    }
}
