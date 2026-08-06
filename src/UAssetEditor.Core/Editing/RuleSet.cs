using UAssetEditor.Core.Search;

namespace UAssetEditor.Core.Editing;

/// <summary>A named, saveable/reusable batch-edit definition: what to find, and what to do to it.</summary>
public sealed class RuleSet
{
    public string Name { get; init; } = "";
    public required SearchQuery Scope { get; init; }
    public List<EditRule> Rules { get; init; } = new();
}
