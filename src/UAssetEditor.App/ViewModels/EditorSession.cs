using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.Editing;
using UAssetEditor.Core.Search;

namespace UAssetEditor.App.ViewModels;

/// <summary>Everything needed to restore a working session: source/versioning settings, search scope, and the rule list.</summary>
public sealed class EditorSession
{
    public string SourcePath { get; init; } = "";
    public EngineVersion DefaultEngineVersion { get; init; } = EngineVersion.VER_UE4_27;
    public string? UsmapPath { get; init; }
    public bool CreateBackup { get; init; } = true;
    public SearchQuery Scope { get; init; } = new();
    public List<EditRule> Rules { get; init; } = new();
}
