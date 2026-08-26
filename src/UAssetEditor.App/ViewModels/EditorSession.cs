using System.Collections.ObjectModel;
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
    public TreeSelectionAction SelectedTreeAction { get; init; } = TreeSelectionAction.LoadSelected;
    public SearchQuery Scope { get; init; } = new();
    public Collection<EditRule> Rules { get; init; } = new();

    /// <summary>Most-recently-opened sources (folder/.pak/.uasset), newest first - see <see cref="MainViewModel.AddRecentSource"/>.</summary>
    public Collection<RecentSourceEntry> RecentSources { get; init; } = new();
}

/// <summary>
/// One Recent Sources entry - captures the engine version, AES key, and usmap that were in
/// effect when this source was last opened alongside its path, since those are per-game/
/// per-pak settings that would otherwise have to be re-typed by hand every time, defeating
/// the point of "recent."
/// </summary>
public sealed record RecentSourceEntry(string SourcePath, EngineVersion EngineVersion, string AesKeyHex, string? UsmapPath)
{
    public string DisplayName => System.IO.Path.GetFileName(SourcePath);
}
