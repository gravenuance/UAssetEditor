using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetEditor.App.Views;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.AssetSources.IoStore;
using UAssetEditor.Core.Editing;
using UAssetEditor.Core.PropertyAccess;
using UAssetEditor.Core.Search;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.App.ViewModels;

public enum RuleKind
{
    SetValue,
    NumericAdjust,
    ReplaceText,
    RemoveProperty,
    AddTag,
    RemoveTag,
    ReplaceReference,
}

public sealed record RuleListItem(string Description, EditRule Rule);

/// <summary>One selectable entry in the batch-edit rule-kind dropdown, pairing the enum value with a human-readable label/explanation - the bare enum name (e.g. "NumericAdjust") isn't self-explanatory on its own.</summary>
public sealed record RuleKindOption(RuleKind Value, string Label, string Description);

public sealed record EngineVersionOption(EngineVersion Value, string Label);

/// <summary>What a tree-driven "open"/"load" populated the results grid with - a whole export (PropertyPath null) or just one table's own subtree, so a later refresh (UE version/usmap/AES change) can replay exactly that, not more.</summary>
public sealed record OpenedScope(string AssetPath, int ExportIndex, string? PropertyPath);

/// <summary>
/// The tree's checkbox-driven actions - previously four separate always-visible buttons, now
/// one dropdown plus a single Run button (see <see cref="MainViewModel.RunSelectedTreeActionCommand"/>)
/// so the toolbar doesn't grow a new button every time a new checkbox-selection action is
/// added (as happened with <see cref="ConvertSelected"/>).
/// </summary>
public enum TreeSelectionAction
{
    LoadSelected,
    ExtractSelected,
    RepackSelected,
    ConvertSelected,
}

/// <summary>One selectable entry in the tree-action dropdown, pairing the enum value with the same label its old dedicated button used to show.</summary>
public sealed record TreeSelectionActionOption(TreeSelectionAction Value, string Label);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UAssetEditor", "lastSession.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly HashSet<string> _dirtyAssetPaths = new();

    /// <summary>
    /// Pak-backed asset paths saved (via <see cref="SaveAllEditedAsync"/>) since the current
    /// <see cref="PakAssetSource"/> was opened, but not yet folded into a real .pak by
    /// <see cref="RepackAsync"/> - a pak-backed Save only writes to that source's private,
    /// per-open temp extraction copy (see <see cref="PakAssetSource"/>'s own docs), so these
    /// are silently lost the moment the source is discarded (reload, close, or app exit)
    /// unless Repack ran first. Cleared in <see cref="DisposeCurrentSource"/> - the one place
    /// that actually discards the source - and checked in <see cref="ConfirmDiscardDirtyEdits"/>
    /// so every path that can discard the source warns about it first.
    /// </summary>
    private readonly HashSet<string> _unrepackedSavedPaths = new();

    private AssetWorkspace? _workspace;
    private IAssetSource? _currentSource;
    private PakAssetSource? _currentPakSource;
    private CancellationTokenSource? _cts;

    /// <summary>Set only while browsing a raw, not-yet-converted IoStore container (<see cref="LoadIoStoreAsync"/>) - the .utoc path <see cref="ConvertSelectedCommand"/> converts from. Null the rest of the time, including once conversion hands off to a normal loose-folder workspace.</summary>
    private string? _ioStoreContainerPath;

    /// <summary>Set once <see cref="ConvertSelectedCommand"/> successfully hands off to a loose-folder workspace at this temp path - see <see cref="DisposeCurrentSource"/>, which is what actually deletes it once the workspace moves on.</summary>
    private string? _ioStoreConvertedTempDir;

    // What to replay against the reopened context after a UE version/usmap/AES change -
    // whichever of these ran most recently, mirroring how SearchResults itself is always
    // fully replaced (not merged) by either action. Both null means nothing to refresh.
    private SearchQuery? _lastSearchQuery;
    private List<OpenedScope> _lastOpenedExports = new();

    // Usmap path and AES key are free-text fields bound with UpdateSourceTrigger=
    // PropertyChanged, so they fire on every keystroke - debounced so a reload doesn't
    // fire (and pop a discard-changes prompt) mid-typing. Engine version is a discrete
    // ComboBox selection and reloads immediately, no debounce needed.
    private readonly DispatcherTimer _reloadDebounceTimer;
    private bool _pendingAesReload;

    /// <summary>
    /// Set while <see cref="OpenRecentAsync"/> is applying a Recent entry's saved engine
    /// version/AES key/usmap ahead of the source switch it's about to perform - without this,
    /// each setter's own change handler would kick off a reload of the *old* (about-to-be-
    /// replaced) workspace first, popping a spurious discard-changes prompt and doing
    /// throwaway work that <see cref="LoadSourceAsync"/> immediately supersedes.
    /// </summary>
    private bool _suppressReloadOnSettingsChange;

    /// <summary>A folder, a .pak archive, a single loose .uasset, or a .utoc IoStore container - auto-detected when <see cref="LoadSourceCommand"/> runs.</summary>
    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private EngineVersion _defaultEngineVersion = EngineVersion.VER_UE4_27;
    [ObservableProperty] private string? _usmapPath;

    [ObservableProperty] private string _pakAesKeyHex = "";
    [ObservableProperty] private bool _isPakBacked;

    /// <summary>
    /// True only while browsing a raw, not-yet-converted IoStore container's listing - there's
    /// no <see cref="AssetWorkspace"/> in that state (nothing is parseable pre-conversion), so
    /// every command that needs a real parsed asset or pak-specific operation is disabled (see
    /// <see cref="CanEditWorkspace"/>) rather than just failing at runtime with a status
    /// message. <see cref="LoadSourceCommand"/> and <see cref="ConvertSelectedCommand"/> are
    /// deliberately exempt - those are exactly what this mode is for.
    /// </summary>
    [NotifyCanExecuteChangedFor(
        nameof(SearchCommand), nameof(PreviewCommand), nameof(ApplyCommand), nameof(SaveAllEditedCommand),
        nameof(RevertEditsCommand), nameof(RepackCommand), nameof(RepackSelectedCommand), nameof(LoadSelectedCommand),
        nameof(OpenFromTreeCommand), nameof(ExtractSelectedCommand), nameof(UnpackPakCommand), nameof(PackFolderCommand),
        nameof(ConvertIoStoreToLegacyCommand), nameof(AddRuleCommand), nameof(RemoveRuleCommand), nameof(RunSelectedTreeActionCommand))]
    [ObservableProperty] private bool _isIoStoreBrowsing;

    public ObservableCollection<AssetTreeItemViewModel> RootTreeItems { get; } = new();

    public IReadOnlyList<TreeSelectionActionOption> TreeSelectionActionOptions { get; } =
    [
        new(TreeSelectionAction.LoadSelected, "Load Selected"),
        new(TreeSelectionAction.ExtractSelected, "Extract Selected..."),
        new(TreeSelectionAction.RepackSelected, "Repack Selected..."),
        new(TreeSelectionAction.ConvertSelected, "Convert Selected..."),
    ];

    /// <summary>Which tree action Run executes - defaults to the last one actually run (persisted via <see cref="BuildSession"/>/<see cref="ApplySession"/>, same as every other per-user preference this session restores), so a workflow the user repeats stays one click instead of a re-pick every time.</summary>
    [NotifyCanExecuteChangedFor(nameof(RunSelectedTreeActionCommand))]
    [ObservableProperty] private TreeSelectionAction _selectedTreeAction = TreeSelectionAction.LoadSelected;

    /// <summary>Most-recently-opened sources, newest first - see <see cref="AddRecentSource"/>.</summary>
    public ObservableCollection<RecentSourceEntry> RecentSources { get; } = new();

    /// <summary>Label for the toolbar's Recent chip - avoids an indexer binding (which throws on an empty collection) for the common "nothing opened yet" state.</summary>
    public string RecentSourceLabel => RecentSources.Count == 0 ? "Recent: none yet" : $"Recent: {RecentSources[0].DisplayName}";

    /// <summary>
    /// Backing collections for the scope row's tag/chip inputs - mutated directly (Add/Remove)
    /// by <see cref="Controls.TermsBox"/> rather than through commands, the same
    /// direct-mutation pattern already used by <see cref="RecentSources"/>. Each term carries
    /// its own AND/OR/NOT tag (see <see cref="ConditionTermViewModel"/>) rather than one
    /// logic setting applying to the whole field.
    /// </summary>
    public ObservableCollection<ConditionTermViewModel> ExportNameTerms { get; } = new();
    public ObservableCollection<ConditionTermViewModel> PropertyNameTerms { get; } = new();
    public ObservableCollection<ConditionTermViewModel> ValueTerms { get; } = new();
    public ObservableCollection<ConditionTermViewModel> ReferenceTerms { get; } = new();

    /// <summary>Whether the floating Edit Rules panel is open - defaults to closed since it overlays the Search Results grid rather than resizing it, so it's opt-in per session rather than always in the way.</summary>
    [ObservableProperty] private bool _isRulesPaneExpanded;

    [NotifyPropertyChangedFor(
        nameof(SelectedRuleKindDescription), nameof(ShowRuleValue1), nameof(RuleValue1Label),
        nameof(ShowRuleValue2), nameof(RuleValue2Label), nameof(ShowRuleOperation), nameof(ShowRuleRegex))]
    [ObservableProperty] private RuleKind _selectedRuleKind = RuleKind.SetValue;

    /// <summary>Shown next to the rule-kind dropdown so what each option actually does isn't a guessing game from the enum name alone.</summary>
    public string SelectedRuleKindDescription =>
        RuleKindOptions.First(o => o.Value == SelectedRuleKind).Description;

    // Which rule-builder fields are actually meaningful depends entirely on the selected
    // kind - e.g. the numeric Operation dropdown (set/add/sub/mul/div) only means anything
    // for Numeric Adjust, and showing it (still selectable) alongside every other kind
    // invited picking something like "mul" while "Replace Text" was selected, which does
    // nothing. Every kind but Remove Property needs RuleValue1; only Replace Text and
    // Replace Reference need RuleValue2, Regex, or a second value at all.
    public bool ShowRuleValue1 => SelectedRuleKind != RuleKind.RemoveProperty;
    public bool ShowRuleValue2 => SelectedRuleKind is RuleKind.ReplaceText or RuleKind.ReplaceReference;
    public bool ShowRuleOperation => SelectedRuleKind == RuleKind.NumericAdjust;
    public bool ShowRuleRegex => SelectedRuleKind is RuleKind.ReplaceText or RuleKind.ReplaceReference;

    public string RuleValue1Label => SelectedRuleKind switch
    {
        RuleKind.SetValue => "New value:",
        RuleKind.NumericAdjust => "Target value:",
        RuleKind.ReplaceText => "Find pattern:",
        RuleKind.AddTag => "Tag to add:",
        RuleKind.RemoveTag => "Tag to remove:",
        RuleKind.ReplaceReference => "Old reference:",
        _ => "Value:",
    };

    public string RuleValue2Label => SelectedRuleKind switch
    {
        RuleKind.ReplaceText => "Replacement:",
        RuleKind.ReplaceReference => "New reference:",
        _ => "Value:",
    };

    [ObservableProperty] private string _ruleValue1 = "";
    [ObservableProperty] private string _ruleValue2 = "";
    [ObservableProperty] private bool _ruleIsRegex;
    [ObservableProperty] private string _ruleOperation = "set";
    [ObservableProperty] private bool _ruleUseSkip;
    [ObservableProperty] private SkipComparison _ruleSkipComparison = SkipComparison.Eq;
    [ObservableProperty] private string _ruleSkipValue = "";

    [ObservableProperty] private bool _createBackup = true;

    [NotifyCanExecuteChangedFor(
        nameof(SearchCommand), nameof(PreviewCommand), nameof(ApplyCommand), nameof(SaveAllEditedCommand),
        nameof(RevertEditsCommand), nameof(RepackCommand), nameof(LoadSourceCommand), nameof(CloseWorkspaceCommand),
        nameof(OpenFromTreeCommand), nameof(LoadSelectedCommand), nameof(ExtractSelectedCommand), nameof(RepackSelectedCommand),
        nameof(ConvertSelectedCommand), nameof(RepackToIoStoreCommand), nameof(UnpackPakCommand), nameof(PackFolderCommand),
        nameof(ConvertIoStoreToLegacyCommand), nameof(AddRuleCommand), nameof(RemoveRuleCommand), nameof(RunSelectedTreeActionCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// Bound to the results grid's IsEnabled (inverted from <see cref="IsBusy"/>) so a
    /// cell edit can't land while a background Search/Save/Preview/Apply is reading or
    /// writing the same live asset graph from another thread - CanExecute on the commands
    /// only stops a *new* background op from starting, it doesn't stop editing while one
    /// already in flight.
    /// </summary>
    public bool IsIdle => !IsBusy;

    /// <summary>True only while a Search/Preview/Apply is actually in flight (i.e. <see cref="_cts"/> is live) - drives the Cancel buttons' visibility, so Cancel doesn't sit around offering to stop something that isn't cancelable (like loading a source, which doesn't use <see cref="_cts"/> at all).</summary>
    [NotifyPropertyChangedFor(nameof(IsNotCancelable))]
    [ObservableProperty] private bool _isCancelable;

    /// <summary>Drives Revert's visibility in the slot Cancel occupies while busy - the two are never meaningful at the same time, so they share a column instead of competing for space.</summary>
    public bool IsNotCancelable => !IsCancelable;

    [ObservableProperty] private string _statusMessage = "Ready.";
    [ObservableProperty] private int _progressCompleted;
    [ObservableProperty] private int _progressTotal;

    public IReadOnlyList<EngineVersionOption> EngineVersionOptions { get; } = BuildEngineVersionOptions();

    public IReadOnlyList<RuleKindOption> RuleKindOptions { get; } =
    [
        new(RuleKind.SetValue, "Set Value", "Replace the property's value outright with a fixed new value."),
        new(RuleKind.NumericAdjust, "Numeric Adjust", "Add, subtract, multiply, or divide the property's numeric value."),
        new(RuleKind.ReplaceText, "Replace Text", "Find and replace a substring (or regex) within the property's text value."),
        new(RuleKind.RemoveProperty, "Remove Property", "Delete the property entirely from the export."),
        new(RuleKind.AddTag, "Add Tag", "Add a tag to a Tags array property."),
        new(RuleKind.RemoveTag, "Remove Tag", "Remove a tag from a Tags array property."),
        new(RuleKind.ReplaceReference, "Replace Reference", "Replace one object/asset reference with another, wherever it's used."),
    ];

    public IReadOnlyList<SkipComparison> SkipComparisons { get; } = Enum.GetValues<SkipComparison>();
    public IReadOnlyList<string> NumericOperations { get; } = new[] { "set", "add", "sub", "mul", "div" };

    public ObservableCollection<SearchResultRow> SearchResults { get; } = new();
    public ObservableCollection<RuleListItem> Rules { get; } = new();
    public ObservableCollection<PropertyChange> PreviewChanges { get; } = new();

    public MainViewModel()
    {
        _reloadDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _reloadDebounceTimer.Tick += ReloadDebounceTimer_Tick;

        if (File.Exists(ConfigPath))
            LoadConfig();
    }

    /// <summary>
    /// UAssetAPI's <see cref="EngineVersion"/> enum mixes real per-release values (VER_UE4_27, VER_UE5_3, ...)
    /// with sentinels that aren't selectable versions (UNKNOWN, VER_UE4_OLDEST_LOADABLE_PACKAGE) and an alias
    /// that duplicates whichever release is newest (VER_UE4_AUTOMATIC_VERSION, VER_UE4_AUTOMATIC_VERSION_PLUS_ONE).
    /// Keep only the values whose name follows the "VER_UE{major}_{minor}[EA]" pattern and turn them into
    /// readable labels like "Unreal Engine 5.0 Early Access".
    /// </summary>
    private static List<EngineVersionOption> BuildEngineVersionOptions()
    {
        var options = new List<EngineVersionOption>();
        foreach (var version in Enum.GetValues<EngineVersion>())
        {
            var match = EngineVersionNamePattern().Match(version.ToString());
            if (!match.Success)
                continue;

            var suffix = match.Groups["ea"].Success ? " Early Access" : "";
            options.Add(new EngineVersionOption(version, $"Unreal Engine {match.Groups["major"].Value}.{match.Groups["minor"].Value}{suffix}"));
        }

        return options;
    }

    [GeneratedRegex(@"^VER_UE(?<major>\d+)_(?<minor>\d+)(?<ea>EA)?$")]
    private static partial Regex EngineVersionNamePattern();

    partial void OnDefaultEngineVersionChanged(EngineVersion value)
    {
        if (!_suppressReloadOnSettingsChange) _ = ReloadContextAsync(aesKeyChanged: false);
    }

    partial void OnUsmapPathChanged(string? value)
    {
        if (!_suppressReloadOnSettingsChange) ScheduleDebouncedReload(aesKeyChanged: false);
    }

    partial void OnPakAesKeyHexChanged(string value)
    {
        if (!_suppressReloadOnSettingsChange) ScheduleDebouncedReload(aesKeyChanged: true);
    }

    private void ScheduleDebouncedReload(bool aesKeyChanged)
    {
        _pendingAesReload = _pendingAesReload || aesKeyChanged;
        _reloadDebounceTimer.Stop();
        _reloadDebounceTimer.Start();
    }

    private async void ReloadDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _reloadDebounceTimer.Stop();
        var aesKeyChanged = _pendingAesReload;
        _pendingAesReload = false;
        await ReloadContextAsync(aesKeyChanged);
    }

    /// <summary>
    /// Reacts to the user changing the UE version, usmap, or (for a pak-backed workspace)
    /// AES key without requiring a restart: an AES key change can only take effect by
    /// re-opening the pak (the key is baked into the native reader at construction), so
    /// that path rebuilds the source and tree from scratch; a version/usmap-only change
    /// reuses the existing source and just drops every already-open asset so the next
    /// access re-parses it under the new settings. Either way, whatever was last shown
    /// (a search, or one opened tree entry) is then replayed so the visible content
    /// actually reflects the change instead of silently going stale.
    /// </summary>
    private async Task ReloadContextAsync(bool aesKeyChanged)
    {
        if (_workspace == null) return; // nothing open yet - new settings apply automatically the next time something opens
        if (IsBusy) return; // don't interrupt in-flight work; settings still apply on the next explicit action

        if (!ConfirmDiscardDirtyEdits("Reload now and discard unsaved edits?"))
            return;

        IsBusy = true;
        StatusMessage = "Reloading with updated settings...";
        try
        {
            _dirtyAssetPaths.Clear();

            if (aesKeyChanged && IsPakBacked && _currentPakSource != null)
            {
                var pakPath = _currentPakSource.PakPath;
                var aesKey = ParseAesKey(PakAesKeyHex);

                DisposeCurrentSource();

                var pakSource = await Task.Run(() => new PakAssetSource(pakPath, aesKey));
                _currentSource = pakSource;
                _currentPakSource = pakSource;
                _workspace = new AssetWorkspace(pakSource, BuildVersionResolver());
                await RebuildTreeAsync(pakSource.ListAllEntries(), '/');
            }
            else
            {
                _workspace.UpdateVersionResolver(BuildVersionResolver());
            }

            await RefreshOpenContentAsync();
            StatusMessage = "Reloaded with updated settings.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Reload failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Re-runs whichever of the last search or last opened tree entry populated the results grid, against the (just-reloaded) workspace.</summary>
    private async Task RefreshOpenContentAsync()
    {
        var workspace = _workspace;
        if (workspace == null) return;

        if (_lastSearchQuery != null)
        {
            var results = await workspace.SearchAsync(_lastSearchQuery, maxDegreeOfParallelism: 0);
            SearchResults.Clear();
            foreach (var result in results)
                SearchResults.Add(new SearchResultRow(result, workspace, OnResultRowDirty));
        }
        else if (_lastOpenedExports.Count > 0)
        {
            var scopes = _lastOpenedExports;
            SearchResults.Clear();
            foreach (var scope in scopes)
            {
                var results = await Task.Run(() =>
                {
                    var asset = workspace.GetOrOpen(scope.AssetPath);
                    return scope.PropertyPath == null
                        ? SearchService.PropertiesForExport(asset, scope.AssetPath, scope.ExportIndex).ToList()
                        : SearchService.PropertiesUnder(asset, scope.AssetPath, scope.ExportIndex, scope.PropertyPath).ToList();
                });
                foreach (var result in results)
                    SearchResults.Add(new SearchResultRow(result, workspace, OnResultRowDirty));
            }
        }
        else
        {
            SearchResults.Clear();
        }
    }

    [RelayCommand]
    private void BrowseSourceFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Select asset folder" };
        if (dialog.ShowDialog() == true)
            SourcePath = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowseSourceFile()
    {
        var dialog = new OpenFileDialog { Title = "Select .pak, .uasset, or .utoc file", Filter = "Pak/Uasset/IoStore files (*.pak;*.uasset;*.utoc)|*.pak;*.uasset;*.utoc|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
            SourcePath = dialog.FileName;
    }

    [RelayCommand]
    private void BrowseUsmap()
    {
        var dialog = new OpenFileDialog { Title = "Select .usmap mappings file", Filter = "Usmap files (*.usmap)|*.usmap|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
            UsmapPath = dialog.FileName;
    }

    /// <summary>Loads whatever <see cref="SourcePath"/> points at - a folder, a .pak archive, a single loose .uasset, or a raw IoStore container - auto-detected, so one path/browse/load flow covers all four instead of needing a separate control per kind.</summary>
    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
    private async Task LoadSourceAsync()
    {
        if (Directory.Exists(SourcePath))
            await LoadFolderAsync(SourcePath);
        else if (File.Exists(SourcePath) && SourcePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            await LoadPakAsync(SourcePath);
        else if (File.Exists(SourcePath) && SourcePath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            await LoadSingleFileAsync(SourcePath);
        else if (File.Exists(SourcePath) && SourcePath.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
            await LoadIoStoreAsync(SourcePath);
        else
            StatusMessage = "Enter or browse to a valid folder, .pak file, .uasset file, or .utoc file.";
    }

    /// <summary>Opens a loose-folder workspace and populates the tree from every file under it - not just .uasset entries, so the tree mirrors the real folder structure.</summary>
    private async Task LoadFolderAsync(string folderPath)
    {
        if (!ConfirmDiscardDirtyEdits()) return;

        IsBusy = true;
        StatusMessage = "Loading folder tree...";
        try
        {
            DisposeCurrentSource();
            _dirtyAssetPaths.Clear();
            SearchResults.Clear();
            _lastSearchQuery = null;
            _lastOpenedExports = [];
            IsIoStoreBrowsing = false;
            _ioStoreContainerPath = null;

            var source = new LooseFolderAssetSource(folderPath);
            _currentSource = source;
            _workspace = new AssetWorkspace(source, BuildVersionResolver());
            IsPakBacked = false;

            // Enumerates only files (not EnumerateFileSystemEntries + a per-entry
            // Directory.Exists check) - halves the filesystem stat calls for large
            // Content trees, and directory nodes are inferred from path segments anyway.
            // Made root-relative (not the raw absolute paths EnumerateFiles returns) so
            // the tree branches the same way a pak's tree does for an equivalent layout,
            // and so leaf FullPaths match LooseFolderAssetSource's root-relative identity.
            var relativePaths = await Task.Run(() =>
                Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(folderPath, p).Replace(Path.DirectorySeparatorChar, '/'))
                    .ToList());
            await RebuildTreeAsync(relativePaths, '/');
            SourcePath = folderPath;
            AddRecentSource(folderPath);
            StatusMessage = "Folder loaded.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load folder: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RepackToIoStoreCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task LoadPakAsync(string pakPath)
    {
        if (!ConfirmDiscardDirtyEdits()) return;

        IsBusy = true;
        StatusMessage = "Opening pak...";
        try
        {
            DisposeCurrentSource();
            _dirtyAssetPaths.Clear();
            SearchResults.Clear();
            _lastSearchQuery = null;
            _lastOpenedExports = [];
            IsIoStoreBrowsing = false;
            _ioStoreContainerPath = null;

            var aesKey = ParseAesKey(PakAesKeyHex);
            var pakSource = await Task.Run(() => new PakAssetSource(pakPath, aesKey));
            _currentSource = pakSource;
            _currentPakSource = pakSource;
            _workspace = new AssetWorkspace(pakSource, BuildVersionResolver());
            IsPakBacked = true;

            await RebuildTreeAsync(pakSource.ListAllEntries(), '/');
            SourcePath = pakPath;
            AddRecentSource(pakPath);
            StatusMessage = pakSource.IsLargePak
                ? $"Opened pak '{Path.GetFileName(pakPath)}' - entries load on demand."
                : $"Opened pak '{Path.GetFileName(pakPath)}' - fully extracted.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open pak: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RepackToIoStoreCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Opens exactly one loose .uasset file - same tree/browse shape as a folder or pak, just rooted at that one file with no parent folders shown.</summary>
    private async Task LoadSingleFileAsync(string filePath)
    {
        if (!ConfirmDiscardDirtyEdits()) return;

        IsBusy = true;
        StatusMessage = $"Opening '{Path.GetFileName(filePath)}'...";
        try
        {
            DisposeCurrentSource();
            _dirtyAssetPaths.Clear();
            SearchResults.Clear();
            _lastSearchQuery = null;
            _lastOpenedExports = [];
            IsIoStoreBrowsing = false;
            _ioStoreContainerPath = null;

            var source = new SingleFileAssetSource(filePath);
            _currentSource = source;
            _workspace = new AssetWorkspace(source, BuildVersionResolver());
            IsPakBacked = false;

            await RebuildTreeAsync([Path.GetFileName(filePath)], '/');
            SourcePath = filePath;
            AddRecentSource(filePath);
            StatusMessage = $"Opened '{Path.GetFileName(filePath)}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open '{Path.GetFileName(filePath)}': {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RepackToIoStoreCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Opens a raw, not-yet-converted IoStore container: lists its contents via retoc (no
    /// conversion happens here), and shows them read-only in the tree - see
    /// <see cref="ConvertSelectedCommand"/> for turning checked entries into an editable
    /// legacy workspace. Never constructs an <see cref="AssetWorkspace"/> - nothing here is
    /// parseable pre-conversion - so <see cref="IsIoStoreBrowsing"/> disables every command
    /// that would otherwise assume one exists (see <see cref="CanEditWorkspace"/>).
    /// </summary>
    private async Task LoadIoStoreAsync(string utocPath)
    {
        if (!ConfirmDiscardDirtyEdits()) return;

        IsBusy = true;
        StatusMessage = "Listing IoStore container...";
        try
        {
            DisposeCurrentSource();
            _workspace = null;
            _dirtyAssetPaths.Clear();
            SearchResults.Clear();
            _lastSearchQuery = null;
            _lastOpenedExports = [];
            IsPakBacked = false;

            var aesKey = ParseAesKey(PakAesKeyHex);
            var entries = await RetocProcess.ListAsync(utocPath, aesKey);

            _ioStoreContainerPath = utocPath;
            IsIoStoreBrowsing = true;

            await RebuildTreeAsync(entries, '/', asZenEntries: true);
            SourcePath = utocPath;
            AddRecentSource(utocPath);
            StatusMessage = $"Listed {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")} - check items and Convert to edit them.";
        }
        catch (Exception ex)
        {
            IsIoStoreBrowsing = false;
            _ioStoreContainerPath = null;
            StatusMessage = $"Failed to list IoStore container: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RepackToIoStoreCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Opens one export's, or one table's, properties into the results grid, editable in
    /// place - same machinery as a search result row. Since the Browse tree never shows a
    /// scalar leaf property as its own entry (only struct/array/map tables are tree
    /// nodes - see <see cref="AssetTreeItemViewModel.MarkPropertiesLoaded"/>), double-clicking
    /// a Property node is how its own leaf fields are actually reached. Any other tree node
    /// kind is a no-op here; expand/collapse for those is handled by the TreeView itself.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditWorkspace))]
    private async Task OpenFromTreeAsync(AssetTreeItemViewModel? item)
    {
        if (item is not { Kind: TreeNodeKind.Export or TreeNodeKind.Property, FullPath: not null }) return;
        if (_workspace == null)
        {
            StatusMessage = "Open a folder, pak, or file first.";
            return;
        }

        var workspace = _workspace;
        var fullPath = item.FullPath;
        var exportIndex = item.ExportIndex;
        var propertyPath = item.PropertyPath;

        IsBusy = true;
        StatusMessage = $"Opening {fullPath} [{item.Name}]...";
        try
        {
            // Extraction (for a pak entry not yet touched) and parsing both do real disk
            // I/O - off the UI thread so a large/lazy pak entry doesn't freeze the window.
            var results = await Task.Run(() =>
            {
                var asset = workspace.GetOrOpen(fullPath);
                return propertyPath == null
                    ? SearchService.PropertiesForExport(asset, fullPath, exportIndex).ToList()
                    : SearchService.PropertiesUnder(asset, fullPath, exportIndex, propertyPath).ToList();
            });

            SearchResults.Clear();
            foreach (var result in results)
                SearchResults.Add(new SearchResultRow(result, workspace, OnResultRowDirty));
            _lastOpenedExports = [new OpenedScope(fullPath, exportIndex, propertyPath)];
            _lastSearchQuery = null;
            StatusMessage = $"Opened {fullPath} [{item.Name}] ({SearchResults.Count} propert{(SearchResults.Count == 1 ? "y" : "ies")}).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open {fullPath} [{item.Name}]: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Lazily populates one asset's "Exports" tree node with real per-export children the
    /// first time it's expanded - parsing every asset in a large tree up front just to
    /// know its export names would be far too expensive, so this is deferred until the
    /// user actually asks to see it.
    /// </summary>
    public async Task LoadExportsAsync(AssetTreeItemViewModel exportsGroup)
    {
        ArgumentNullException.ThrowIfNull(exportsGroup);

        if (exportsGroup.ExportsLoaded || exportsGroup.AssetPath == null || _workspace == null) return;

        var workspace = _workspace;
        var assetPath = exportsGroup.AssetPath;

        try
        {
            var exportNames = await Task.Run(() =>
            {
                var asset = workspace.GetOrOpen(assetPath);
                return asset.Exports.Select(e => e.ObjectName.Value?.Value ?? "").ToList();
            });
            exportsGroup.MarkExportsLoaded(exportNames);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load exports for {assetPath}: {ex.Message}";
        }
    }

    /// <summary>
    /// Lazily populates one Export or Property tree node with its own top-level property
    /// children the first time it's expanded - an export's own properties for an Export
    /// node, or one level further into a struct/array/map for a Property node. The asset
    /// is already open (its Exports node had to be expanded first to reach either kind of
    /// node), so this never triggers a fresh parse; it's just reading what's already there.
    /// </summary>
    public async Task LoadPropertiesAsync(AssetTreeItemViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.PropertiesLoaded || node.AssetPath == null || _workspace == null) return;
        if (node.Kind is not (TreeNodeKind.Export or TreeNodeKind.Property)) return;

        var workspace = _workspace;
        var assetPath = node.AssetPath;

        try
        {
            var items = await Task.Run(() =>
            {
                var asset = workspace.GetOrOpen(assetPath);
                return node.Kind == TreeNodeKind.Export
                    ? PropertyTreeExpander.GetExportRoot(asset.Exports[node.ExportIndex], asset)
                    : PropertyTreeExpander.GetChildren(node.Property!, node.PropertyPath!, asset);
            });
            node.MarkPropertiesLoaded(items);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load properties for {assetPath}: {ex.Message}";
        }
    }

    /// <summary>
    /// Runs whichever of the four checkbox-selection actions <see cref="SelectedTreeAction"/>
    /// currently holds - the single Run button's target. Delegates to each action's own
    /// existing command method directly (not through its generated ICommand wrapper) so this
    /// stays a plain dispatch with no double command-invocation machinery; <see cref="CanRunSelectedTreeAction"/>
    /// still asks that same underlying command's own CanExecute, so eligibility (e.g. Convert
    /// staying available during IoStore browsing while the other three don't, per
    /// <see cref="CanEditWorkspace"/>) is never duplicated or able to drift out of sync.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunSelectedTreeAction))]
    private async Task RunSelectedTreeActionAsync()
    {
        switch (SelectedTreeAction)
        {
            case TreeSelectionAction.LoadSelected: await LoadSelectedAsync(); break;
            case TreeSelectionAction.ExtractSelected: await ExtractSelectedAsync(); break;
            case TreeSelectionAction.RepackSelected: await RepackSelectedAsync(); break;
            case TreeSelectionAction.ConvertSelected: await ConvertSelectedAsync(); break;
        }
    }

    private bool CanRunSelectedTreeAction() => SelectedTreeAction switch
    {
        TreeSelectionAction.LoadSelected => LoadSelectedCommand.CanExecute(null),
        TreeSelectionAction.ExtractSelected => ExtractSelectedCommand.CanExecute(null),
        TreeSelectionAction.RepackSelected => RepackSelectedCommand.CanExecute(null),
        TreeSelectionAction.ConvertSelected => ConvertSelectedCommand.CanExecute(null),
        _ => false,
    };

    /// <summary>
    /// Loads every checked export's or table's properties into the grid at once. A whole
    /// Export node is always checkable (see <see cref="AssetTreeItemViewModel.IsCheckable"/>);
    /// a Property (table) node is checkable only when it has editable content somewhere in
    /// its own subtree, so a table that only serves to hold other tables never clutters up
    /// a multi-selection with nothing to actually load. By the time either kind of node
    /// exists to check, its asset was already parsed (expanding "Exports" is what loads
    /// exports; expanding an export is what loads its tables), so this never triggers a
    /// parse of its own - it just gathers what's already known. Replaces the grid's current
    /// contents, same as a single tree-driven open.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditWorkspace))]
    private async Task LoadSelectedAsync()
    {
        if (_workspace == null)
        {
            StatusMessage = "Load a folder, .pak file, or .uasset file first.";
            return;
        }

        var workspace = _workspace;
        var toLoad = new List<OpenedScope>();

        void Collect(AssetTreeItemViewModel node)
        {
            // Folder/Asset nodes are also checkable now (for ExtractSelectedCommand - see
            // AssetTreeItemViewModel.IsCheckable), so this must filter to Export/Property
            // explicitly rather than just "checked with a path" - otherwise a folder checked
            // for extraction would also get swept in here as a bogus load target.
            if (node.IsChecked && node.FullPath != null && node.Kind is TreeNodeKind.Export or TreeNodeKind.Property)
            {
                toLoad.Add(node.Kind == TreeNodeKind.Export
                    ? new OpenedScope(node.FullPath, node.ExportIndex, null)
                    : new OpenedScope(node.FullPath, node.ExportIndex, node.PropertyPath!));
            }

            foreach (var child in node.Children)
                Collect(child);
        }

        foreach (var root in RootTreeItems)
            Collect(root);

        var distinct = toLoad.Distinct().ToList();
        if (distinct.Count == 0)
        {
            StatusMessage = "Check one or more exports or tables in the tree.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Loading {distinct.Count} item(s)...";
        try
        {
            SearchResults.Clear();
            foreach (var scope in distinct)
            {
                var results = await Task.Run(() =>
                {
                    var asset = workspace.GetOrOpen(scope.AssetPath);
                    return scope.PropertyPath == null
                        ? SearchService.PropertiesForExport(asset, scope.AssetPath, scope.ExportIndex).ToList()
                        : SearchService.PropertiesUnder(asset, scope.AssetPath, scope.ExportIndex, scope.PropertyPath).ToList();
                });
                foreach (var result in results)
                    SearchResults.Add(new SearchResultRow(result, workspace, OnResultRowDirty));
            }
            _lastOpenedExports = distinct;
            _lastSearchQuery = null;
            StatusMessage = $"Loaded {distinct.Count} item(s) ({SearchResults.Count} propert{(SearchResults.Count == 1 ? "y" : "ies")} total).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load selection: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Extracts every checked Folder/Asset node's subtree to a chosen destination folder -
    /// the FModel-style "check what you want, extract it" workflow, layered entirely on
    /// <see cref="PakUnpacker"/>'s entry filter (pak-backed sources) with no new native
    /// surface of its own. A loose-folder-backed source needs no pak/worker involvement at
    /// all - it's a plain recursive file copy.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditWorkspace))]
    private async Task ExtractSelectedAsync()
    {
        if (_currentSource == null)
        {
            StatusMessage = "Load a folder, .pak file, or .uasset file first.";
            return;
        }

        var prefixes = CollectSelectedPrefixes();
        if (prefixes.Count == 0)
        {
            StatusMessage = "Check one or more folders or assets in the tree first.";
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Extract selected to..." };
        if (dialog.ShowDialog() != true) return;
        var destination = dialog.FolderName;

        IsBusy = true;
        StatusMessage = "Extracting...";
        try
        {
            if (_currentPakSource != null)
            {
                var pakSource = _currentPakSource;
                var result = await Task.Run(() =>
                    PakUnpacker.Unpack(pakSource, destination, entryFilter: entry => MatchesAnySelectedPrefix(entry, prefixes)));

                StatusMessage = result.HasFailures
                    ? $"Extracted {result.SucceededCount} file(s) to {destination} - {result.FailedEntries.Count} failed."
                    : $"Extracted {result.SucceededCount} file(s) to {destination}.";
            }
            else
            {
                var sourceRoot = SourcePath;
                var count = await Task.Run(() => CopySelectedLooseFiles(sourceRoot, destination, prefixes));
                StatusMessage = $"Extracted {count} file(s) to {destination}.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Extract failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Builds a new .pak containing only the checked Folder/Asset subtrees, at the source
    /// pak's own version and mount point - the "ship a partial combination of my mod's
    /// changes without hand-extracting and repacking" workflow: reuses the exact same
    /// checkbox selection as <see cref="ExtractSelectedCommand"/>, just handed to
    /// <see cref="PakRepacker"/>'s entry filter instead of <see cref="PakUnpacker"/>'s, so a
    /// user who edited features A, B, and C can produce an "A+C only" pak (say) without ever
    /// leaving the app.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditWorkspace))]
    private async Task RepackSelectedAsync()
    {
        if (_currentPakSource == null)
        {
            StatusMessage = "Open a .pak first.";
            return;
        }

        var prefixes = CollectSelectedPrefixes();
        if (prefixes.Count == 0)
        {
            StatusMessage = "Check one or more folders or assets in the tree first.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Repack selected to...",
            Filter = "Pak files (*.pak)|*.pak",
            FileName = Path.GetFileName(_currentPakSource.PakPath),
        };
        if (dialog.ShowDialog() != true) return;

        var source = _currentPakSource;
        var outputPath = dialog.FileName;

        IsBusy = true;
        StatusMessage = "Repacking selected...";
        try
        {
            var aesKey = ParseAesKey(PakAesKeyHex);
            var result = await Task.Run(() =>
                PakRepacker.Build(source, outputPath, aesKey: aesKey, entryFilter: entry => MatchesAnySelectedPrefix(entry, prefixes)));

            // Unlike a full Repack, this only ever bakes in the selected subset - only
            // remove those specific paths from the at-risk set, so anything still
            // saved-but-unrepacked outside the selection keeps warning correctly.
            if (!result.HasFailures)
                foreach (var path in source.ListAllEntries().Where(e => MatchesAnySelectedPrefix(e, prefixes)))
                    _unrepackedSavedPaths.Remove(path);

            StatusMessage = result.HasFailures
                ? $"Repack failed: {result.FailedEntries[0].Reason}"
                : $"Repacked {result.SucceededCount} selected file(s) to {outputPath}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Repack failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Every checked Folder/Asset node's FullPath, shared by <see cref="ExtractSelectedCommand"/>
    /// and <see cref="RepackSelectedCommand"/>. For a pak-backed source, a checked .uasset leaf
    /// also pulls in its .uexp/.ubulk companion entries (see
    /// <see cref="PakAssetSource.CompanionExtensions"/>) - those hold the asset's actual
    /// exported/bulk data and aren't separately checkable nodes in the tree, but leaving them
    /// out produces a broken asset in the output.
    /// </summary>
    private List<string> CollectSelectedPrefixes()
    {
        var prefixes = new List<string>();
        void Collect(AssetTreeItemViewModel node)
        {
            if (node.IsChecked && node.FullPath != null && node.Kind is TreeNodeKind.Folder or TreeNodeKind.Asset)
            {
                prefixes.Add(node.FullPath);

                if (_currentPakSource != null && node.Kind == TreeNodeKind.Asset && node.FullPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                {
                    var baseNoExt = node.FullPath[..^".uasset".Length];
                    foreach (var companionExt in PakAssetSource.CompanionExtensions)
                    {
                        var companionPath = baseNoExt + companionExt;
                        if (_currentPakSource.ListAllEntries().Contains(companionPath))
                            prefixes.Add(companionPath);
                    }
                }
            }

            foreach (var child in node.Children)
                Collect(child);
        }
        foreach (var root in RootTreeItems)
            Collect(root);
        return prefixes;
    }

    /// <summary>
    /// Every checked <see cref="TreeNodeKind.ZenAsset"/> leaf's <c>FullPath</c>, for
    /// <see cref="ConvertSelectedCommand"/> - expands a checked Folder node into every
    /// ZenAsset leaf underneath it (same "checking a folder means everything under it"
    /// convention <see cref="CollectSelectedPrefixes"/> already uses for pak/loose sources),
    /// rather than passing folder-level prefixes straight to retoc's own -f/--filter, whose
    /// matching semantics for a folder prefix aren't documented.
    /// </summary>
    private List<string> CollectSelectedZenAssetPaths()
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        void Collect(AssetTreeItemViewModel node, bool parentChecked)
        {
            var included = parentChecked || node.IsChecked;
            if (included && node.Kind == TreeNodeKind.ZenAsset && node.FullPath != null)
                selected.Add(node.FullPath);

            foreach (var child in node.Children)
                Collect(child, included && node.Kind == TreeNodeKind.Folder);
        }
        foreach (var root in RootTreeItems)
            Collect(root, parentChecked: false);
        return selected.ToList();
    }

    /// <summary>
    /// Converts the checked entries of a raw IoStore container (see
    /// <see cref="LoadIoStoreAsync"/>) into legacy-format loose files via retoc's to-legacy,
    /// then immediately opens the result through the normal <see cref="LoadFolderAsync"/>
    /// pipeline - the whole point of converting is to hand off to the editing machinery this
    /// app already has, not to build a second one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
    private async Task ConvertSelectedAsync()
    {
        if (_ioStoreContainerPath == null)
        {
            StatusMessage = "Load a .utoc file first.";
            return;
        }

        var selected = CollectSelectedZenAssetPaths();
        if (selected.Count == 0)
        {
            StatusMessage = "Check one or more entries in the tree first.";
            return;
        }

        var utocPath = _ioStoreContainerPath;
        var tempDir = Path.Combine(Path.GetTempPath(), "UAssetEditor_IoStore_" + Guid.NewGuid());
        var aesKey = ParseAesKey(PakAesKeyHex);
        var count = selected.Count;

        IsBusy = true;
        StatusMessage = $"Converting {count} entr{(count == 1 ? "y" : "ies")} to legacy format...";
        try
        {
            await RetocProcess.ConvertToLegacyAsync(utocPath, tempDir, selected, aesKey);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Convert failed: {ex.Message}";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        StatusMessage = $"Converted {count} entr{(count == 1 ? "y" : "ies")} - opening...";
        await LoadFolderAsync(tempDir);

        // Only track tempDir for cleanup once it's actually the live workspace - if
        // LoadFolderAsync declined (unsaved edits in whatever was open before) or threw,
        // _currentSource never became tempDir's LooseFolderAssetSource, and DisposeCurrentSource
        // deleting it out from under that untouched previous workspace would be wrong. In that
        // case nothing will ever open tempDir, so it's cleaned up directly here instead.
        if (_currentSource is LooseFolderAssetSource)
        {
            _ioStoreConvertedTempDir = tempDir;
        }
        else
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort - a leftover temp folder isn't worth failing over */ }
        }
    }

    private static bool MatchesAnySelectedPrefix(string entry, List<string> prefixes) =>
        prefixes.Any(p => entry.Equals(p, StringComparison.OrdinalIgnoreCase) || entry.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));

    private static int CopySelectedLooseFiles(string sourceRoot, string destination, List<string> prefixes)
    {
        var count = 0;
        foreach (var prefix in prefixes)
        {
            var sourcePath = Path.Combine(sourceRoot, prefix.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(sourcePath))
            {
                var destPath = Path.Combine(destination, prefix.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Copy(sourcePath, destPath, overwrite: true);
                count++;
            }
            else if (Directory.Exists(sourcePath))
            {
                foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(sourceRoot, file);
                    var destPath = Path.Combine(destination, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    File.Copy(file, destPath, overwrite: true);
                    count++;
                }
            }
        }
        return count;
    }

    [RelayCommand(CanExecute = nameof(CanEditWorkspace))]
    private async Task RepackAsync()
    {
        if (_currentPakSource == null)
        {
            StatusMessage = "Open a .pak first.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Repack to...",
            Filter = "Pak files (*.pak)|*.pak",
            FileName = Path.GetFileName(_currentPakSource.PakPath),
        };
        if (dialog.ShowDialog() != true) return;

        var source = _currentPakSource;
        var outputPath = dialog.FileName;

        IsBusy = true;
        StatusMessage = "Repacking...";
        try
        {
            var aesKey = ParseAesKey(PakAesKeyHex);
            var result = await Task.Run(() => PakRepacker.Build(source, outputPath, aesKey: aesKey));

            // A failed Repack discards its partial output entirely (see PakRepacker) - so
            // only a clean run actually folded every saved-but-unrepacked edit into a real
            // .pak; anything short of that leaves them exactly as at-risk as before.
            if (!result.HasFailures)
                _unrepackedSavedPaths.Clear();

            StatusMessage = result.HasFailures
                ? $"Repack failed: {result.FailedEntries[0].Reason}"
                : $"Repacked to {outputPath}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Repack failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Converts the current workspace back into a fresh .utoc/.ucas pair via retoc's to-zen -
    /// from a loose-folder workspace (whether that folder came from <see cref="ConvertSelectedCommand"/>
    /// or was opened by hand) or from an open legacy .pak. retoc's to-zen technically accepts a
    /// .pak directly, but its package-path derivation for UE4.27-and-earlier containers needs
    /// every asset's project-relative path to include a project-name segment (see
    /// <see cref="RetocDirectoryInputResolver"/>) - a real pak's own entries are stored relative
    /// to ITS mount point instead, without that segment baked in, so a pak-backed workspace is
    /// extracted into a correctly-named temp folder first rather than handed to retoc directly.
    /// A loose-folder workspace goes through <see cref="RetocDirectoryInputResolver.Resolve"/>
    /// the same way <see cref="PackFolderViewModel"/> does. Always writes to a new file, matching
    /// every other repack path's convention of never overwriting the source.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRepackToIoStore))]
    private async Task RepackToIoStoreAsync()
    {
        string defaultFileName;
        if (_currentSource is LooseFolderAssetSource)
        {
            defaultFileName = Path.GetFileNameWithoutExtension(SourcePath.TrimEnd('\\', '/'));
        }
        else if (_currentPakSource != null)
        {
            defaultFileName = Path.GetFileNameWithoutExtension(_currentPakSource.PakPath);
        }
        else
        {
            StatusMessage = "Open a loose folder, a .pak, or convert IoStore entries first.";
            return;
        }

        var retocVersion = EngineVersionMapping.ToRetocVersion(DefaultEngineVersion);
        if (retocVersion == null)
        {
            StatusMessage = "Pick a UE4.25+ engine version - this one has no IoStore equivalent.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Repack to IoStore...",
            Filter = "IoStore container (*.utoc)|*.utoc",
            FileName = defaultFileName + ".utoc",
        };
        if (dialog.ShowDialog() != true) return;

        var outputPath = dialog.FileName;

        IsBusy = true;
        string? tempRoot = null;
        try
        {
            var aesKey = ParseAesKey(PakAesKeyHex);

            string input;
            if (_currentSource is LooseFolderAssetSource)
            {
                var resolved = RetocDirectoryInputResolver.Resolve(SourcePath);
                if (resolved == null)
                {
                    StatusMessage = "Pick a workspace folder that isn't a drive root.";
                    return;
                }
                input = resolved;
            }
            else
            {
                var pakSource = _currentPakSource!;
                StatusMessage = "Extracting pak for IoStore conversion...";
                var projectName = RetocDirectoryInputResolver.ExtractProjectName(pakSource.MountPoint)
                    ?? Path.GetFileNameWithoutExtension(pakSource.PakPath);
                tempRoot = Path.Combine(Path.GetTempPath(), "UAssetEditor_ToZen_" + Guid.NewGuid());
                var projectFolder = Path.Combine(tempRoot, projectName);
                Directory.CreateDirectory(projectFolder);
                var unpackResult = await Task.Run(() => PakUnpacker.Unpack(pakSource, projectFolder));
                if (unpackResult.HasFailures)
                {
                    StatusMessage = $"Extract failed: {unpackResult.FailedEntries[0].Reason}";
                    return;
                }
                input = tempRoot;
            }

            StatusMessage = "Repacking to IoStore...";
            await RetocProcess.ConvertToZenAsync(input, outputPath, retocVersion, aesKey);
            StatusMessage = $"Repacked to {outputPath}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Repack to IoStore failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            if (tempRoot != null)
            {
                // A large pak extracts to thousands of files - deleting that tree recursively
                // is slow enough to freeze the UI thread if done inline here, so it runs on a
                // background thread instead. IsBusy is already false above, so this cleanup
                // doesn't block the user from starting something else in the meantime.
                var pathToDelete = tempRoot;
                await Task.Run(() =>
                {
                    try { Directory.Delete(pathToDelete, recursive: true); }
                    catch { /* best effort - a leftover temp extraction isn't worth failing over */ }
                });
            }
        }
    }

    /// <summary>Opens the Convert IoStore to Legacy dialog, pre-filled with the current AES key field - conversion itself runs inside the dialog, not here. Standalone, unlike <see cref="ConvertSelectedCommand"/>, which only converts checked entries of an already-browsed container into the app's own workspace.</summary>
    [RelayCommand(CanExecute = nameof(CanEditWorkspace))]
    private void ConvertIoStoreToLegacy()
    {
        using var viewModel = new ConvertIoStoreToLegacyViewModel(null, PakAesKeyHex);
        new ConvertIoStoreToLegacyWindow { DataContext = viewModel, Owner = Application.Current.MainWindow }.ShowDialog();
    }

    /// <summary>Opens the Unpack .pak dialog, pre-filled with the currently-loaded pak (if any) and the current AES key field - unpacking itself runs inside the dialog, not here.</summary>
    [RelayCommand(CanExecute = nameof(CanEditWorkspace))]
    private void UnpackPak()
    {
        using var viewModel = new UnpackPakViewModel(_currentPakSource?.PakPath, PakAesKeyHex);
        new UnpackPakWindow { DataContext = viewModel, Owner = Application.Current.MainWindow }.ShowDialog();
    }

    /// <summary>Opens the Pack Folder into .pak dialog, pre-filled with the currently-loaded pak's mount point (if any) so a mod folder built from an unpacked pak defaults to repacking back the same way.</summary>
    [RelayCommand(CanExecute = nameof(CanEditWorkspace))]
    private void PackFolder()
    {
        using var viewModel = new PackFolderViewModel(null, _currentPakSource?.MountPoint ?? "../../../Game/", PakAesKeyHex, DefaultEngineVersion,
            mountPointIsAuthoritative: _currentPakSource != null, initialVersion: _currentPakSource?.Version);
        new PackFolderWindow { DataContext = viewModel, Owner = Application.Current.MainWindow }.ShowDialog();
    }

    [RelayCommand]
    private static void ShowAbout() => new AboutWindow { Owner = Application.Current.MainWindow }.ShowDialog();

    [RelayCommand]
    private static void ViewOnGitHub() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/gravenuance/UAssetEditor") { UseShellExecute = true });

    /// <summary>
    /// Reopens a source from the Recent list, restoring the engine version/AES key/usmap that
    /// were in effect when it was last opened (not just the path) - same auto-detect/load path
    /// as typing it into Source and clicking Load, just with the per-game settings pre-filled
    /// instead of whatever's currently in those fields.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
    private async Task OpenRecentAsync(RecentSourceEntry entry)
    {
        _reloadDebounceTimer.Stop();
        _pendingAesReload = false;
        _suppressReloadOnSettingsChange = true;
        try
        {
            SourcePath = entry.SourcePath;
            DefaultEngineVersion = entry.EngineVersion;
            PakAesKeyHex = entry.AesKeyHex;
            UsmapPath = entry.UsmapPath;
        }
        finally
        {
            _suppressReloadOnSettingsChange = false;
        }

        await LoadSourceAsync();
    }

    /// <summary>Tracks the most recently opened sources (newest first, deduplicated by path, capped) for the Recent dropdown, persisted immediately so it survives a restart even without an explicit Save Config.</summary>
    private void AddRecentSource(string path)
    {
        var existing = RecentSources.FirstOrDefault(r => r.SourcePath == path);
        if (existing != null)
            RecentSources.Remove(existing);

        RecentSources.Insert(0, new RecentSourceEntry(path, DefaultEngineVersion, PakAesKeyHex, UsmapPath));
        while (RecentSources.Count > 8)
            RecentSources.RemoveAt(RecentSources.Count - 1);
        OnPropertyChanged(nameof(RecentSourceLabel));
        SaveConfig();
    }

    [RelayCommand(CanExecute = nameof(CanEditWorkspace))]
    private void AddRule()
    {
        EditRule rule = SelectedRuleKind switch
        {
            RuleKind.SetValue => new SetPropertyValueRule { NewValue = RuleValue1, Skip = BuildSkipCondition() },
            RuleKind.NumericAdjust => new NumericAdjustRule { Operation = RuleOperation, TargetValue = RuleValue1, Skip = BuildSkipCondition() },
            RuleKind.ReplaceText => new ReplaceTextRule { Pattern = RuleValue1, Replacement = RuleValue2, IsRegex = RuleIsRegex },
            RuleKind.RemoveProperty => new RemovePropertyRule(),
            RuleKind.AddTag => new AddTagRule { Tag = RuleValue1 },
            RuleKind.RemoveTag => new RemoveTagRule { Tag = RuleValue1 },
            RuleKind.ReplaceReference => new ReplaceReferenceRule { OldReference = RuleValue1, NewReference = RuleValue2, IsRegex = RuleIsRegex },
            _ => throw new NotSupportedException(),
        };

        Rules.Add(new RuleListItem(Describe(rule), rule));
    }

    [RelayCommand(CanExecute = nameof(CanEditWorkspace))]
    private void RemoveRule(RuleListItem item) => Rules.Remove(item);

    [RelayCommand]
    private void PromoteToRule(SearchResultRow? row)
    {
        if (row?.PropertyPath == null) return;

        var leaf = row.PropertyPath.Split('.', '[')[^1].TrimEnd(']');
        if (!PropertyNameTerms.Any(t => string.Equals(t.Text, leaf, StringComparison.OrdinalIgnoreCase)))
            PropertyNameTerms.Add(new ConditionTermViewModel(leaf));

        SelectedRuleKind = RuleKind.SetValue;
        RuleValue1 = row.Value;
        StatusMessage = $"Promoted '{leaf}' into the property-name scope and rule builder.";
    }

    [RelayCommand(CanExecute = nameof(CanEditWorkspace))]
    private async Task SearchAsync()
    {
        if (_workspace == null)
        {
            StatusMessage = "Load a folder, .pak file, or .uasset file first.";
            return;
        }

        var query = BuildScope();
        _lastSearchQuery = query;
        _lastOpenedExports = [];

        SearchResults.Clear();
        IsBusy = true;
        StatusMessage = "Searching...";
        _cts = new CancellationTokenSource();
        IsCancelable = true;
        var progress = new Progress<SearchProgress>(p =>
        {
            ProgressCompleted = p.Completed;
            ProgressTotal = p.Total;
        });

        try
        {
            var results = await _workspace!.SearchAsync(query, progress, maxDegreeOfParallelism: 0, _cts.Token);
            foreach (var result in results)
                SearchResults.Add(new SearchResultRow(result, _workspace!, OnResultRowDirty));
            StatusMessage = $"Found {results.Count} match(es).";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Search canceled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsCancelable = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditWorkspace))]
    private async Task SaveAllEditedAsync()
    {
        if (_workspace == null || _dirtyAssetPaths.Count == 0)
        {
            StatusMessage = "Nothing to save.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Saving edited assets...";
        var dirtyPaths = _dirtyAssetPaths.ToList();

        try
        {
            await Task.Run(() => _workspace.SaveAll(dirtyPaths, CreateBackup, backupFolder: null));

            foreach (var row in SearchResults.Where(r => dirtyPaths.Contains(r.AssetPath)))
                row.IsDirty = false;

            _dirtyAssetPaths.Clear();

            // A pak-backed Save only reaches that pak's own private temp extraction copy,
            // not the real .pak on disk - track it as still-at-risk until an actual Repack
            // folds it in, so ConfirmDiscardDirtyEdits can warn before it's lost.
            if (IsPakBacked)
                foreach (var path in dirtyPaths)
                    _unrepackedSavedPaths.Add(path);

            StatusMessage = IsPakBacked
                ? $"Saved {dirtyPaths.Count} asset(s) to the pak's temp copy."
                : $"Saved {dirtyPaths.Count} asset(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Discards every unsaved edit (from Apply's staged rule matches, or manual grid-cell
    /// edits) and reloads its last-saved (or original, if never saved this session) value
    /// from disk. Cheap by construction: an unsaved edit only ever lives on the workspace's
    /// in-memory cached instance - see <see cref="ApplyAsync"/> and <see cref="SearchResultRow"/>
    /// - so discarding it is just evicting that instance and letting the next access re-parse
    /// the untouched bytes underneath. Deliberately scoped to <see cref="_dirtyAssetPaths"/>
    /// only: an already-saved-but-unrepacked pak edit is a different, harder-to-undo state
    /// (see <see cref="_unrepackedSavedPaths"/>) that this does not touch.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditWorkspace))]
    private async Task RevertEditsAsync()
    {
        if (_workspace == null || _dirtyAssetPaths.Count == 0)
        {
            StatusMessage = "Nothing to revert.";
            return;
        }

        var dirtyPaths = _dirtyAssetPaths.ToList();
        var result = MessageBox.Show(
            $"Discard {dirtyPaths.Count} unsaved edit(s) and reload their last-saved value?",
            "Revert changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        IsBusy = true;
        StatusMessage = "Reverting...";
        try
        {
            foreach (var path in dirtyPaths)
                _workspace.Close(path);
            _dirtyAssetPaths.Clear();

            await RefreshOpenContentAsync();

            StatusMessage = $"Reverted {dirtyPaths.Count} asset(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Revert failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
    private void CloseWorkspace()
    {
        if (!ConfirmDiscardDirtyEdits("Discard unsaved edits and close the workspace?")) return;

        _workspace?.CloseAll();
        DisposeCurrentSource();
        _workspace = null;
        _dirtyAssetPaths.Clear();
        SearchResults.Clear();
        RootTreeItems.Clear();
        _lastSearchQuery = null;
        _lastOpenedExports = [];
        IsPakBacked = false;
        IsIoStoreBrowsing = false;
        _ioStoreContainerPath = null;
        RepackToIoStoreCommand.NotifyCanExecuteChanged();
        StatusMessage = "Workspace closed.";
    }

    /// <summary>Called from App shutdown so a pak-backed workspace's temp extraction folder and native handles don't leak past the process.</summary>
    public void Cleanup() => DisposeCurrentSource();

    /// <summary>Satisfies CA1001 (owns a disposable <see cref="_cts"/> field) - <see cref="Cleanup"/> stays the primary shutdown entry point App.xaml.cs already calls, this just also releases whatever cancellation source is live at the time.</summary>
    public void Dispose()
    {
        _cts?.Dispose();
        Cleanup();
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanEditWorkspace))]
    private async Task PreviewAsync()
    {
        if (_currentSource == null)
        {
            StatusMessage = "Load a folder, .pak file, or .uasset file first.";
            return;
        }

        var source = _currentSource;
        var versions = BuildVersionResolver();
        var ruleSet = BuildRuleSet();

        PreviewChanges.Clear();
        IsBusy = true;
        StatusMessage = "Computing preview...";
        _cts = new CancellationTokenSource();
        IsCancelable = true;
        var progress = new Progress<EditProgress>(p =>
        {
            ProgressCompleted = p.Completed;
            ProgressTotal = p.Total;
        });

        try
        {
            var changeSets = await EditExecutor.PreviewAsync(source, versions, ruleSet, progress, maxDegreeOfParallelism: 0, cancellationToken: _cts.Token);
            foreach (var change in changeSets.SelectMany(c => c.Changes))
                PreviewChanges.Add(change);
            StatusMessage = $"Preview: {changeSets.Count} asset(s), {PreviewChanges.Count} change(s).";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Preview canceled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Preview failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsCancelable = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Runs the batch edit rules and stages the results directly on the workspace's live,
    /// already-cached asset instances - the same objects a manual grid-cell edit mutates -
    /// instead of opening a fresh throwaway copy and saving it immediately. Nothing is
    /// written to disk here: touched assets are just marked dirty (same highlight/tracking
    /// as a manual cell edit) so the user can review the grid and choose to Save All Edited
    /// (or discard by reloading) rather than have a batch of hundreds of edits committed to
    /// disk in one irreversible step.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditWorkspace))]
    private async Task ApplyAsync()
    {
        if (_currentSource == null || _workspace == null)
        {
            StatusMessage = "Load a folder, .pak file, or .uasset file first.";
            return;
        }

        var source = _currentSource;
        var workspace = _workspace;
        var ruleSet = BuildRuleSet();

        IsBusy = true;
        StatusMessage = "Applying changes...";
        _cts = new CancellationTokenSource();
        IsCancelable = true;
        var progress = new Progress<EditProgress>(p =>
        {
            ProgressCompleted = p.Completed;
            ProgressTotal = p.Total;
        });

        try
        {
            var changeSets = await EditExecutor.StageAsync(source, workspace.GetOrOpen, ruleSet, progress: progress, maxDegreeOfParallelism: 0, cancellationToken: _cts.Token);
            var touchedPaths = changeSets.Select(c => c.AssetPath).ToHashSet();

            foreach (var path in touchedPaths)
                _dirtyAssetPaths.Add(path);

            await RefreshOpenContentAsync();

            foreach (var row in SearchResults.Where(r => touchedPaths.Contains(r.AssetPath)))
                row.IsDirty = true;

            // StageAsync opens every asset in the source (to check whether the rules'
            // scope matches it), not just the ones that end up touched - left as-is, a
            // large source would leave its entire contents resident in the workspace
            // cache after one Apply. Only the touched (now dirty) and any pre-existing
            // dirty paths need to stay open; everything else served no further purpose
            // once checked, so it's dropped rather than held onto for free.
            foreach (var path in source.EnumerateAssetPaths())
                if (!touchedPaths.Contains(path) && !_dirtyAssetPaths.Contains(path))
                    workspace.Close(path);

            StatusMessage = changeSets.Count == 0
                ? "Applied: no changes matched."
                : $"Applied changes to {changeSets.Count} asset(s) - not saved yet.";
            SaveConfig();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Apply canceled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Apply failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsCancelable = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void SaveConfig()
    {
        try
        {
            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(BuildSession(), JsonOptions));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save config failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                StatusMessage = "No saved configuration found.";
                return;
            }

            var session = JsonSerializer.Deserialize<EditorSession>(File.ReadAllText(ConfigPath), JsonOptions);
            if (session == null) return;

            ApplySession(session);
            StatusMessage = "Configuration loaded.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load config failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Gates every command that touches the workspace's shared asset graph (search, save,
    /// preview/apply, opening a folder/pak/tree entry, closing) so they can't overlap.
    /// Without this, e.g. editing a grid cell (which mutates a live PropertyData directly)
    /// while a background Save All / Search is reading or serializing that same object
    /// graph is a real data race, not just a UX nicety. Cancel is deliberately excluded -
    /// it must stay clickable while busy.
    /// </summary>
    private bool CanRunWhenIdle() => !IsBusy;

    /// <summary>
    /// Same as <see cref="CanRunWhenIdle"/>, plus excluded while <see cref="IsIoStoreBrowsing"/>:
    /// that mode never constructs an <see cref="AssetWorkspace"/> (raw IoStore chunks aren't
    /// parseable pre-conversion), so nothing that needs a real parsed asset or pak-specific
    /// operation has anything to act on. <see cref="LoadSourceCommand"/> and
    /// <see cref="ConvertSelectedCommand"/> stay on <see cref="CanRunWhenIdle"/> instead -
    /// those are exactly what IoStore-browsing mode is for.
    /// </summary>
    private bool CanEditWorkspace() => !IsBusy && !IsIoStoreBrowsing;

    /// <summary>Repack to IoStore works from either a loose-folder-backed workspace or an open legacy .pak - retoc's to-zen accepts both a directory and a .pak as input directly, so either source can go straight to IoStore.</summary>
    private bool CanRepackToIoStore() => !IsBusy && (_currentSource is LooseFolderAssetSource || _currentPakSource != null);

    private void OnResultRowDirty(SearchResultRow row) => _dirtyAssetPaths.Add(row.AssetPath);

    private bool ConfirmDiscardDirtyEdits(string message = "Discard unsaved edits and continue?")
    {
        if (_dirtyAssetPaths.Count > 0)
        {
            var result = MessageBox.Show(message, "Unsaved changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return false;
        }

        if (_unrepackedSavedPaths.Count > 0)
        {
            var result = MessageBox.Show(
                $"Continue and lose {_unrepackedSavedPaths.Count} unrepacked asset(s)?",
                "Unrepacked changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return false;
        }

        return true;
    }

    private void DisposeCurrentSource()
    {
        if (_currentSource is IDisposable disposable)
            disposable.Dispose();
        _currentSource = null;
        _currentPakSource = null;
        _unrepackedSavedPaths.Clear();

        // ConvertSelectedCommand's temp conversion folder has no owner of its own the way
        // PakAssetSource owns (and cleans up) its TempExtractionDirectory - LooseFolderAssetSource
        // is deliberately not IDisposable, since for a real user-opened folder deleting it on
        // close would be catastrophic. Only clean this one up specifically, and only once the
        // workspace has actually moved on from it (this runs at the start of every Load*Async
        // and from CloseWorkspace/Cleanup), so repeated convert-browse-convert cycles don't
        // silently accumulate full copies of converted asset data across a session.
        if (_ioStoreConvertedTempDir != null)
        {
            try { Directory.Delete(_ioStoreConvertedTempDir, recursive: true); }
            catch { /* best effort - a leftover temp folder isn't worth failing over */ }
            _ioStoreConvertedTempDir = null;
        }
    }

    /// <summary>
    /// Sorting/grouping every path into a <see cref="PathTreeNode"/> tree and wrapping each
    /// node in an <see cref="AssetTreeItemViewModel"/> (built eagerly, recursively, for the
    /// whole folder/file structure - see that type's own doc comment) is pure CPU work that
    /// used to run straight on the UI thread after the actually-async I/O finished, which is
    /// what froze the window on a large pak/folder. Both steps move into <see cref="Task.Run"/>
    /// here; only handing the finished items to <see cref="RootTreeItems"/> stays on the UI
    /// thread, since that collection is what the TreeView is actually bound to.
    /// </summary>
    private async Task RebuildTreeAsync(IEnumerable<string> paths, char separator, bool asZenEntries = false)
    {
        var items = await Task.Run(() =>
        {
            var root = PathTreeBuilder.Build(paths, separator);
            return root.Children.Select(child => new AssetTreeItemViewModel(child, asZenEntries)).ToList();
        });

        RootTreeItems.Clear();
        foreach (var item in items)
            RootTreeItems.Add(item);
    }

    private static byte[]? ParseAesKey(string hex) => PakAesKey.Parse(hex);

    private EngineVersionResolver BuildVersionResolver()
    {
        var resolver = new EngineVersionResolver { DefaultVersion = DefaultEngineVersion };
        if (!string.IsNullOrWhiteSpace(UsmapPath) && File.Exists(UsmapPath))
            resolver.Mappings = new Usmap(UsmapPath);
        return resolver;
    }

    private SearchQuery BuildScope() => new()
    {
        ExportNameTerms = ExportNameTerms.Select(t => t.ToCore()).ToList(),
        PropertyNameTerms = PropertyNameTerms.Select(t => t.ToCore()).ToList(),
        ValueTerms = ValueTerms.Select(t => t.ToCore()).ToList(),
        ReferenceTerms = ReferenceTerms.Select(t => t.ToCore()).ToList(),
    };

    private RuleSet BuildRuleSet() => new()
    {
        Scope = BuildScope(),
        Rules = new Collection<EditRule>(Rules.Select(r => r.Rule).ToList()),
    };

    private SkipCondition? BuildSkipCondition() =>
        RuleUseSkip && !string.IsNullOrWhiteSpace(RuleSkipValue)
            ? new SkipCondition { Comparison = RuleSkipComparison, Value = RuleSkipValue }
            : null;

    private EditorSession BuildSession() => new()
    {
        SourcePath = SourcePath,
        DefaultEngineVersion = DefaultEngineVersion,
        UsmapPath = UsmapPath,
        CreateBackup = CreateBackup,
        SelectedTreeAction = SelectedTreeAction,
        Scope = BuildScope(),
        Rules = new Collection<EditRule>(Rules.Select(r => r.Rule).ToList()),
        RecentSources = new Collection<RecentSourceEntry>(RecentSources.ToList()),
    };

    private void ApplySession(EditorSession session)
    {
        SourcePath = session.SourcePath;
        DefaultEngineVersion = session.DefaultEngineVersion;
        UsmapPath = session.UsmapPath;
        CreateBackup = session.CreateBackup;
        SelectedTreeAction = session.SelectedTreeAction;

        ExportNameTerms.Clear();
        foreach (var term in session.Scope.ExportNameTerms) ExportNameTerms.Add(new ConditionTermViewModel(term.Text, term.Tag));
        PropertyNameTerms.Clear();
        foreach (var term in session.Scope.PropertyNameTerms) PropertyNameTerms.Add(new ConditionTermViewModel(term.Text, term.Tag));
        ValueTerms.Clear();
        foreach (var term in session.Scope.ValueTerms) ValueTerms.Add(new ConditionTermViewModel(term.Text, term.Tag));
        ReferenceTerms.Clear();
        foreach (var term in session.Scope.ReferenceTerms) ReferenceTerms.Add(new ConditionTermViewModel(term.Text, term.Tag));

        Rules.Clear();
        foreach (var rule in session.Rules)
            Rules.Add(new RuleListItem(Describe(rule), rule));

        RecentSources.Clear();
        foreach (var recent in session.RecentSources)
            RecentSources.Add(recent);
        OnPropertyChanged(nameof(RecentSourceLabel));
    }

    private static string Describe(EditRule rule) => rule switch
    {
        SetPropertyValueRule r => $"Set value = \"{r.NewValue}\"{DescribeSkip(r.Skip)}",
        NumericAdjustRule r => $"{r.Operation} {r.TargetValue}{DescribeSkip(r.Skip)}",
        ReplaceTextRule r => $"Replace \"{r.Pattern}\" -> \"{r.Replacement}\"" + (r.IsRegex ? " (regex)" : ""),
        RemovePropertyRule => "Remove property",
        AddTagRule r => $"Add tag \"{r.Tag}\"",
        RemoveTagRule r => $"Remove tag \"{r.Tag}\"",
        ReplaceReferenceRule r => $"Replace reference \"{r.OldReference}\" -> \"{r.NewReference}\"" + (r.IsRegex ? " (regex)" : ""),
        _ => rule.GetType().Name,
    };

    private static string DescribeSkip(SkipCondition? skip) =>
        skip == null ? "" : $" (skip if {skip.Comparison} {skip.Value})";
}
