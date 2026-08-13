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
using UAssetEditor.Core.AssetSources;
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

public partial class MainViewModel : ObservableObject
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UAssetEditor", "lastSession.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly EditExecutor _editExecutor = new();
    private readonly SearchService _searchService = new();
    private readonly HashSet<string> _dirtyAssetPaths = new();

    private AssetWorkspace? _workspace;
    private IAssetSource? _currentSource;
    private PakAssetSource? _currentPakSource;
    private CancellationTokenSource? _cts;

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

    /// <summary>A folder, a .pak archive, or a single loose .uasset - auto-detected when <see cref="LoadSourceCommand"/> runs.</summary>
    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private EngineVersion _defaultEngineVersion = EngineVersion.VER_UE4_27;
    [ObservableProperty] private string? _usmapPath;

    [ObservableProperty] private string _pakAesKeyHex = "";
    [ObservableProperty] private bool _isPakBacked;

    public ObservableCollection<AssetTreeItemViewModel> RootTreeItems { get; } = new();

    [ObservableProperty] private string _exportNameConditionsText = "";
    [ObservableProperty] private MatchLogic _exportNameLogic = MatchLogic.Or;
    [ObservableProperty] private string _propertyNameConditionsText = "";
    [ObservableProperty] private MatchLogic _propertyNameLogic = MatchLogic.Or;
    [ObservableProperty] private string _valueConditionsText = "";
    [ObservableProperty] private MatchLogic _valueLogic = MatchLogic.Or;
    [ObservableProperty] private string _referenceConditionsText = "";
    [ObservableProperty] private MatchLogic _referenceLogic = MatchLogic.Or;

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
        nameof(RepackCommand), nameof(LoadSourceCommand), nameof(CloseWorkspaceCommand),
        nameof(OpenFromTreeCommand), nameof(LoadSelectedCommand))]
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
    [ObservableProperty] private bool _isCancelable;

    [ObservableProperty] private string _statusMessage = "Ready.";
    [ObservableProperty] private int _progressCompleted;
    [ObservableProperty] private int _progressTotal;

    public IReadOnlyList<EngineVersionOption> EngineVersionOptions { get; } = BuildEngineVersionOptions();

    public IReadOnlyList<RuleKindOption> RuleKindOptions { get; } =
    [
        new(RuleKind.SetValue, "Set Value", "Replace the property's value outright with a fixed new value."),
        new(RuleKind.NumericAdjust, "Numeric Adjust", "Add, subtract, multiply, or divide the property's current numeric value (pick the operation)."),
        new(RuleKind.ReplaceText, "Replace Text", "Find and replace a substring (or regex) within the property's text value."),
        new(RuleKind.RemoveProperty, "Remove Property", "Delete the property entirely from the export."),
        new(RuleKind.AddTag, "Add Tag", "Add a tag to a Tags array property."),
        new(RuleKind.RemoveTag, "Remove Tag", "Remove a tag from a Tags array property."),
        new(RuleKind.ReplaceReference, "Replace Reference", "Replace one object/asset reference with another, wherever it's used."),
    ];

    public IReadOnlyList<MatchLogic> MatchLogics { get; } = Enum.GetValues<MatchLogic>();
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

    partial void OnDefaultEngineVersionChanged(EngineVersion value) => _ = ReloadContextAsync(aesKeyChanged: false);

    partial void OnUsmapPathChanged(string? value) => ScheduleDebouncedReload(aesKeyChanged: false);

    partial void OnPakAesKeyHexChanged(string value) => ScheduleDebouncedReload(aesKeyChanged: true);

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

        if (!ConfirmDiscardDirtyEdits("Changing the engine version, usmap, or AES key requires reloading already-open assets. Discard unsaved edits and reload now?"))
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
                RebuildTree(pakSource.ListAllEntries(), '/');
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
                        ? _searchService.PropertiesForExport(asset, scope.AssetPath, scope.ExportIndex).ToList()
                        : _searchService.PropertiesUnder(asset, scope.AssetPath, scope.ExportIndex, scope.PropertyPath).ToList();
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
        var dialog = new OpenFileDialog { Title = "Select .pak or .uasset file", Filter = "Pak/Uasset files (*.pak;*.uasset)|*.pak;*.uasset|All files (*.*)|*.*" };
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

    /// <summary>Loads whatever <see cref="SourcePath"/> points at - a folder, a .pak archive, or a single loose .uasset - auto-detected, so one path/browse/load flow covers all three instead of needing a separate control per kind.</summary>
    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
    private async Task LoadSourceAsync()
    {
        if (Directory.Exists(SourcePath))
            await LoadFolderAsync(SourcePath);
        else if (File.Exists(SourcePath) && SourcePath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
            await LoadPakAsync(SourcePath);
        else if (File.Exists(SourcePath) && SourcePath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            LoadSingleFile(SourcePath);
        else
            StatusMessage = "Enter or browse to a valid folder, .pak file, or .uasset file.";
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
            RebuildTree(relativePaths, '/');
            StatusMessage = "Folder loaded.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load folder: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
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

            var aesKey = ParseAesKey(PakAesKeyHex);
            var pakSource = await Task.Run(() => new PakAssetSource(pakPath, aesKey));
            _currentSource = pakSource;
            _currentPakSource = pakSource;
            _workspace = new AssetWorkspace(pakSource, BuildVersionResolver());
            IsPakBacked = true;

            RebuildTree(pakSource.ListAllEntries(), '/');
            StatusMessage = $"Opened pak '{Path.GetFileName(pakPath)}' " +
                (pakSource.IsLargePak ? "(large - entries extract on open)." : "(fully extracted).");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open pak: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens exactly one loose .uasset file - same tree/browse shape as a folder or pak, just rooted at that one file with no parent folders shown.</summary>
    private void LoadSingleFile(string filePath)
    {
        if (!ConfirmDiscardDirtyEdits()) return;

        DisposeCurrentSource();
        _dirtyAssetPaths.Clear();
        SearchResults.Clear();
        _lastSearchQuery = null;
        _lastOpenedExports = [];

        var source = new SingleFileAssetSource(filePath);
        _currentSource = source;
        _workspace = new AssetWorkspace(source, BuildVersionResolver());
        IsPakBacked = false;

        RebuildTree([Path.GetFileName(filePath)], '/');
        StatusMessage = $"Opened '{Path.GetFileName(filePath)}'.";
    }

    /// <summary>
    /// Opens one export's, or one table's, properties into the results grid, editable in
    /// place - same machinery as a search result row. Since the Browse tree never shows a
    /// scalar leaf property as its own entry (only struct/array/map tables are tree
    /// nodes - see <see cref="AssetTreeItemViewModel.MarkPropertiesLoaded"/>), double-clicking
    /// a Property node is how its own leaf fields are actually reached. Any other tree node
    /// kind is a no-op here; expand/collapse for those is handled by the TreeView itself.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
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
                    ? _searchService.PropertiesForExport(asset, fullPath, exportIndex).ToList()
                    : _searchService.PropertiesUnder(asset, fullPath, exportIndex, propertyPath).ToList();
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
    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
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
            if (node.IsChecked && node.FullPath != null)
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
            StatusMessage = "Check one or more exports or tables in the tree first (expand an asset's Exports node to see them).";
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
                        ? _searchService.PropertiesForExport(asset, scope.AssetPath, scope.ExportIndex).ToList()
                        : _searchService.PropertiesUnder(asset, scope.AssetPath, scope.ExportIndex, scope.PropertyPath).ToList();
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

    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
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
            await Task.Run(() => PakRepacker.Build(source, outputPath, aesKey: aesKey));
            StatusMessage = $"Repacked to {outputPath}.";
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

    [RelayCommand]
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

    [RelayCommand]
    private void RemoveRule(RuleListItem item) => Rules.Remove(item);

    [RelayCommand]
    private void PromoteToRule(SearchResultRow? row)
    {
        if (row?.PropertyPath == null) return;

        var leaf = row.PropertyPath.Split('.', '[')[^1].TrimEnd(']');
        var existing = ParseLines(PropertyNameConditionsText);
        if (!existing.Contains(leaf, StringComparer.OrdinalIgnoreCase))
        {
            PropertyNameConditionsText = string.IsNullOrEmpty(PropertyNameConditionsText)
                ? leaf
                : PropertyNameConditionsText + Environment.NewLine + leaf;
        }

        SelectedRuleKind = RuleKind.SetValue;
        RuleValue1 = row.Value;
        StatusMessage = $"Promoted '{leaf}' into the property-name scope and rule builder.";
    }

    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
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
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
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
            StatusMessage = $"Saved {dirtyPaths.Count} asset(s).";
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
        StatusMessage = "Workspace closed.";
    }

    /// <summary>Called from App shutdown so a pak-backed workspace's temp extraction folder and native handles don't leak past the process.</summary>
    public void Cleanup() => DisposeCurrentSource();

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
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
            var changeSets = await _editExecutor.PreviewAsync(source, versions, ruleSet, progress, maxDegreeOfParallelism: 0, cancellationToken: _cts.Token);
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
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
    private async Task ApplyAsync()
    {
        if (_currentSource == null)
        {
            StatusMessage = "Load a folder, .pak file, or .uasset file first.";
            return;
        }

        var source = _currentSource;
        var versions = BuildVersionResolver();
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
            var changeSets = await _editExecutor.ApplyAsync(source, versions, ruleSet, CreateBackup, backupFolder: null, progress: progress, maxDegreeOfParallelism: 0, cancellationToken: _cts.Token);
            StatusMessage = $"Applied changes to {changeSets.Count} asset(s).";
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

    private void OnResultRowDirty(SearchResultRow row) => _dirtyAssetPaths.Add(row.AssetPath);

    private bool ConfirmDiscardDirtyEdits(string message = "The current workspace has unsaved edits. Discard them and continue?")
    {
        if (_dirtyAssetPaths.Count == 0) return true;

        var result = MessageBox.Show(message, "Unsaved changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    private void DisposeCurrentSource()
    {
        if (_currentSource is IDisposable disposable)
            disposable.Dispose();
        _currentSource = null;
        _currentPakSource = null;
    }

    private void RebuildTree(IEnumerable<string> paths, char separator)
    {
        RootTreeItems.Clear();
        var root = PathTreeBuilder.Build(paths, separator);
        foreach (var child in root.Children)
            RootTreeItems.Add(new AssetTreeItemViewModel(child));
    }

    private static byte[]? ParseAesKey(string hex)
    {
        hex = hex.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        return hex.Length == 0 ? null : Convert.FromHexString(hex);
    }

    private EngineVersionResolver BuildVersionResolver()
    {
        var resolver = new EngineVersionResolver { DefaultVersion = DefaultEngineVersion };
        if (!string.IsNullOrWhiteSpace(UsmapPath) && File.Exists(UsmapPath))
            resolver.Mappings = new Usmap(UsmapPath);
        return resolver;
    }

    private SearchQuery BuildScope() => new()
    {
        ExportNamePatterns = ParseLines(ExportNameConditionsText),
        ExportNameLogic = ExportNameLogic,
        PropertyNamePatterns = ParseLines(PropertyNameConditionsText),
        PropertyNameLogic = PropertyNameLogic,
        ValuePatterns = ParseLines(ValueConditionsText),
        ValueLogic = ValueLogic,
        ReferencePatterns = ParseLines(ReferenceConditionsText),
        ReferenceLogic = ReferenceLogic,
    };

    private RuleSet BuildRuleSet() => new()
    {
        Scope = BuildScope(),
        Rules = Rules.Select(r => r.Rule).ToList(),
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
        Scope = BuildScope(),
        Rules = Rules.Select(r => r.Rule).ToList(),
    };

    private void ApplySession(EditorSession session)
    {
        SourcePath = session.SourcePath;
        DefaultEngineVersion = session.DefaultEngineVersion;
        UsmapPath = session.UsmapPath;
        CreateBackup = session.CreateBackup;

        ExportNameConditionsText = JoinLines(session.Scope.ExportNamePatterns);
        ExportNameLogic = session.Scope.ExportNameLogic;
        PropertyNameConditionsText = JoinLines(session.Scope.PropertyNamePatterns);
        PropertyNameLogic = session.Scope.PropertyNameLogic;
        ValueConditionsText = JoinLines(session.Scope.ValuePatterns);
        ValueLogic = session.Scope.ValueLogic;
        ReferenceConditionsText = JoinLines(session.Scope.ReferencePatterns);
        ReferenceLogic = session.Scope.ReferenceLogic;

        Rules.Clear();
        foreach (var rule in session.Rules)
            Rules.Add(new RuleListItem(Describe(rule), rule));
    }

    private static List<string> ParseLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToList();

    private static string JoinLines(IReadOnlyList<string> lines) => string.Join(Environment.NewLine, lines);

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
