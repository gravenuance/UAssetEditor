using System.Collections.ObjectModel;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.Editing;
using UAssetEditor.Core.Search;

namespace UAssetEditor.App.ViewModels;

/// <summary>Everything needed to restore a working session: source/versioning settings, search scope, and the rule list.</summary>
public sealed class EditorSession
{
    /// <summary>Bumped whenever a breaking shape change needs <see cref="MainViewModel"/>'s load path to migrate an older file forward - see its LoadConfig.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Defaults to 0 (not <see cref="CurrentSchemaVersion"/>) so a config file saved before this field existed reads back as "unversioned" rather than silently appearing current.</summary>
    public int SchemaVersion { get; init; }

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
