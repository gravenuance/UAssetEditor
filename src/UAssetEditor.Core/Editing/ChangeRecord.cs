namespace UAssetEditor.Core.Editing;

public sealed record PropertyChange(
    string AssetPath,
    int ExportIndex,
    string ExportName,
    string? PropertyPath,
    string RuleDescription,
    string OldValue,
    string NewValue);

/// <summary>What a rule set did to one asset.</summary>
/// <param name="Error">
/// Why the asset could not be opened (<paramref name="Skipped"/>), or why editing or saving it failed. After a failure
/// <paramref name="Changes"/> lists only what was applied in memory before it, and nothing of it was saved.
/// </param>
/// <param name="Skipped">The asset could not be opened, so no rule could apply to it; reported, but not a failure of the run.</param>
public sealed record AssetChangeSet(string AssetPath, IReadOnlyList<PropertyChange> Changes, string? Error = null, bool Skipped = false)
{
    public bool Failed => Error != null && !Skipped;

    public bool Changed => Error == null;
}

public readonly record struct EditProgress(int Completed, int Total, string CurrentAssetPath);
