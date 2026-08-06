namespace UAssetEditor.Core.Editing;

public sealed record PropertyChange(
    string AssetPath,
    int ExportIndex,
    string ExportName,
    string? PropertyPath,
    string RuleDescription,
    string OldValue,
    string NewValue);

public sealed record AssetChangeSet(string AssetPath, IReadOnlyList<PropertyChange> Changes);

public readonly record struct EditProgress(int Completed, int Total, string CurrentAssetPath);
