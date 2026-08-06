using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Editing;
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
    private string? _workspaceRootFolder;
    private IAssetSource? _currentSource;
    private PakAssetSource? _currentPakSource;
    private CancellationTokenSource? _cts;

    // What to replay against the reopened context after a UE version/usmap/AES change -
    // whichever of these ran most recently, mirroring how SearchResults itself is always
    // fully replaced (not merged) by either action. Both null means nothing to refresh.
    private SearchQuery? _lastSearchQuery;
    private (string AssetPath, int ExportIndex)? _lastOpenedExport;

    // Usmap path and AES key are free-text fields bound with UpdateSourceTrigger=
    // PropertyChanged, so they fire on every keystroke - debounced so a reload doesn't
    // fire (and pop a discard-changes prompt) mid-typing. Engine version is a discrete
    // ComboBox selection and reloads immediately, no debounce needed.
    private readonly DispatcherTimer _reloadDebounceTimer;
    private bool _pendingAesReload;

    [ObservableProperty] private string _rootFolder = "";
    [ObservableProperty] private EngineVersion _defaultEngineVersion = EngineVersion.VER_UE4_27;
    [ObservableProperty] private string? _usmapPath;
    [ObservableProperty] private int _maxDegreeOfParallelism; // 0 = adaptive (responds to memory pressure); >0 = fixed cap

    [ObservableProperty] private string _pakPath = "";
    [ObservableProperty] private string _pakAesKeyHex = "";
    [ObservableProperty] private bool _isPakBacked;

    [ObservableProperty] private string _singleFilePath = "";

    public ObservableCollection<AssetTreeItemViewModel> RootTreeItems { get; } = new();

    [ObservableProperty] private string _exportNameConditionsText = "";
    [ObservableProperty] private MatchLogic _exportNameLogic = MatchLogic.Or;
    [ObservableProperty] private string _propertyNameConditionsText = "";
    [ObservableProperty] private MatchLogic _propertyNameLogic = MatchLogic.Or;
    [ObservableProperty] private string _valueConditionsText = "";
    [ObservableProperty] private MatchLogic _valueLogic = MatchLogic.Or;
    [ObservableProperty] private string _referenceConditionsText = "";
    [ObservableProperty] private MatchLogic _referenceLogic = MatchLogic.Or;

    [ObservableProperty] private RuleKind _selectedRuleKind = RuleKind.SetValue;
    [ObservableProperty] private string _ruleValue1 = "";
    [ObservableProperty] private string _ruleValue2 = "";
    [ObservableProperty] private bool _ruleIsRegex;
    [ObservableProperty] private string _ruleOperation = "set";
    [ObservableProperty] private bool _ruleUseSkip;
    [ObservableProperty] private SkipComparison _ruleSkipComparison = SkipComparison.Eq;
    [ObservableProperty] private string _ruleSkipValue = "";

    [ObservableProperty] private bool _createBackup = true;
    [ObservableProperty] private string? _backupFolder;

    [NotifyCanExecuteChangedFor(
        nameof(SearchCommand), nameof(PreviewCommand), nameof(ApplyCommand), nameof(SaveAllEditedCommand),
        nameof(RepackCommand), nameof(OpenPakCommand), nameof(OpenFolderCommand), nameof(OpenFileCommand), nameof(CloseWorkspaceCommand),
        nameof(OpenFromTreeCommand), nameof(BrowseFolderCommand))]
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

    [ObservableProperty] private string _statusMessage = "Ready.";
    [ObservableProperty] private int _progressCompleted;
    [ObservableProperty] private int _progressTotal;

    public IReadOnlyList<EngineVersion> EngineVersions { get; } = Enum.GetValues<EngineVersion>();
    public IReadOnlyList<RuleKind> RuleKinds { get; } = Enum.GetValues<RuleKind>();
    public IReadOnlyList<MatchLogic> MatchLogics { get; } = Enum.GetValues<MatchLogic>();
    public IReadOnlyList<SkipComparison> SkipComparisons { get; } = Enum.GetValues<SkipComparison>();
    public IReadOnlyList<string> NumericOperations { get; } = new[] { "set", "add", "sub", "mul", "div" };

    /// <summary>Whether the results grid's Asset column should be shown - only earns its place when results span more than one asset (e.g. a batch Search), not when browsing a single opened export.</summary>
    [ObservableProperty] private bool _showAssetColumn;

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
            var results = await workspace.SearchAsync(_lastSearchQuery, maxDegreeOfParallelism: MaxDegreeOfParallelism);
            SearchResults.Clear();
            foreach (var result in results)
                SearchResults.Add(new SearchResultRow(result, workspace, OnResultRowDirty));
            UpdateShowAssetColumn();
        }
        else if (_lastOpenedExport != null)
        {
            var (path, exportIndex) = _lastOpenedExport.Value;
            var results = await Task.Run(() =>
            {
                var asset = workspace.GetOrOpen(path);
                return _searchService.PropertiesForExport(asset, path, exportIndex).ToList();
            });
            SearchResults.Clear();
            foreach (var result in results)
                SearchResults.Add(new SearchResultRow(result, workspace, OnResultRowDirty));
            UpdateShowAssetColumn();
        }
        else
        {
            SearchResults.Clear();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
    private async Task BrowseFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Select asset folder" };
        if (dialog.ShowDialog() == true)
        {
            RootFolder = dialog.FolderName;
            await OpenFolderAsync();
        }
    }

    [RelayCommand]
    private void BrowseUsmap()
    {
        var dialog = new OpenFileDialog { Title = "Select .usmap mappings file", Filter = "Usmap files (*.usmap)|*.usmap|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
            UsmapPath = dialog.FileName;
    }

    [RelayCommand]
    private void BrowsePak()
    {
        var dialog = new OpenFileDialog { Title = "Select .pak file", Filter = "Pak files (*.pak)|*.pak|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
            PakPath = dialog.FileName;
    }

    [RelayCommand]
    private void BrowseSingleFile()
    {
        var dialog = new OpenFileDialog { Title = "Select .uasset file", Filter = "Uasset files (*.uasset)|*.uasset|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
            SingleFilePath = dialog.FileName;
    }

    /// <summary>Opens (or reopens) the loose-folder workspace and populates the tree from every file under it - not just .uasset entries, so the tree mirrors the real folder structure.</summary>
    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
    private async Task OpenFolderAsync()
    {
        if (!ValidateRootFolder() || !EnsureWorkspace()) return;

        IsBusy = true;
        StatusMessage = "Loading folder tree...";
        try
        {
            // Enumerates only files (not EnumerateFileSystemEntries + a per-entry
            // Directory.Exists check) - halves the filesystem stat calls for large
            // Content trees, and directory nodes are inferred from path segments anyway.
            // Made root-relative (not the raw absolute paths EnumerateFiles returns) so
            // the tree branches the same way a pak's tree does for an equivalent layout,
            // and so leaf FullPaths match LooseFolderAssetSource's root-relative identity.
            var relativePaths = await Task.Run(() =>
                Directory.EnumerateFiles(RootFolder, "*", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(RootFolder, p).Replace(Path.DirectorySeparatorChar, '/'))
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

    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
    private async Task OpenPakAsync()
    {
        if (!File.Exists(PakPath))
        {
            StatusMessage = "Select a valid .pak file first.";
            return;
        }

        if (!ConfirmDiscardDirtyEdits()) return;

        IsBusy = true;
        StatusMessage = "Opening pak...";
        try
        {
            DisposeCurrentSource();
            _dirtyAssetPaths.Clear();
            SearchResults.Clear();
            _lastSearchQuery = null;
            _lastOpenedExport = null;

            var pakPath = PakPath;
            var aesKey = ParseAesKey(PakAesKeyHex);
            var pakSource = await Task.Run(() => new PakAssetSource(pakPath, aesKey));
            _currentSource = pakSource;
            _currentPakSource = pakSource;
            _workspace = new AssetWorkspace(pakSource, BuildVersionResolver());
            _workspaceRootFolder = null;
            IsPakBacked = true;

            RebuildTree(pakSource.ListAllEntries(), '/');
            StatusMessage = $"Opened pak '{Path.GetFileName(PakPath)}' " +
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
    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
    private void OpenFile()
    {
        if (!File.Exists(SingleFilePath))
        {
            StatusMessage = "Select a valid .uasset file first.";
            return;
        }

        if (!ConfirmDiscardDirtyEdits()) return;

        IsBusy = true;
        StatusMessage = "Opening file...";
        try
        {
            DisposeCurrentSource();
            _dirtyAssetPaths.Clear();
            SearchResults.Clear();
            _lastSearchQuery = null;
            _lastOpenedExport = null;

            var filePath = SingleFilePath;
            var source = new SingleFileAssetSource(filePath);
            _currentSource = source;
            _workspace = new AssetWorkspace(source, BuildVersionResolver());
            _workspaceRootFolder = null;
            IsPakBacked = false;

            RebuildTree([Path.GetFileName(filePath)], '/');
            StatusMessage = $"Opened '{Path.GetFileName(filePath)}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open file: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens one export node's properties, editable in place - same machinery as a search result row. Any other tree node kind is a no-op here; expand/collapse for those is handled by the TreeView itself.</summary>
    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
    private async Task OpenFromTreeAsync(AssetTreeItemViewModel? item)
    {
        if (item is not { Kind: TreeNodeKind.Export, FullPath: not null }) return;
        if (_workspace == null)
        {
            StatusMessage = "Open a folder, pak, or file first.";
            return;
        }

        var workspace = _workspace;
        var fullPath = item.FullPath;
        var exportIndex = item.ExportIndex;

        IsBusy = true;
        StatusMessage = $"Opening {fullPath} [{item.Name}]...";
        try
        {
            // Extraction (for a pak entry not yet touched) and parsing both do real disk
            // I/O - off the UI thread so a large/lazy pak entry doesn't freeze the window.
            var results = await Task.Run(() =>
            {
                var asset = workspace.GetOrOpen(fullPath);
                return _searchService.PropertiesForExport(asset, fullPath, exportIndex).ToList();
            });

            SearchResults.Clear();
            foreach (var result in results)
                SearchResults.Add(new SearchResultRow(result, workspace, OnResultRowDirty));
            UpdateShowAssetColumn();
            _lastOpenedExport = (fullPath, exportIndex);
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
        if (!ValidateRootFolder() || !EnsureWorkspace()) return;

        var query = BuildScope();
        _lastSearchQuery = query;
        _lastOpenedExport = null;

        SearchResults.Clear();
        IsBusy = true;
        StatusMessage = "Searching...";
        _cts = new CancellationTokenSource();
        var progress = new Progress<SearchProgress>(p =>
        {
            ProgressCompleted = p.Completed;
            ProgressTotal = p.Total;
        });

        try
        {
            var results = await _workspace!.SearchAsync(query, progress, MaxDegreeOfParallelism, _cts.Token);
            foreach (var result in results)
                SearchResults.Add(new SearchResultRow(result, _workspace!, OnResultRowDirty));
            UpdateShowAssetColumn();
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
            await Task.Run(() => _workspace.SaveAll(dirtyPaths, CreateBackup, BackupFolder));

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
        _workspaceRootFolder = null;
        _dirtyAssetPaths.Clear();
        SearchResults.Clear();
        RootTreeItems.Clear();
        _lastSearchQuery = null;
        _lastOpenedExport = null;
        ShowAssetColumn = false;
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
        if (_currentSource == null && !ValidateRootFolder()) return;

        var source = ResolveBatchSource();
        var versions = BuildVersionResolver();
        var ruleSet = BuildRuleSet();

        PreviewChanges.Clear();
        IsBusy = true;
        StatusMessage = "Computing preview...";
        _cts = new CancellationTokenSource();
        var progress = new Progress<EditProgress>(p =>
        {
            ProgressCompleted = p.Completed;
            ProgressTotal = p.Total;
        });

        try
        {
            var changeSets = await _editExecutor.PreviewAsync(source, versions, ruleSet, progress, MaxDegreeOfParallelism, _cts.Token);
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
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunWhenIdle))]
    private async Task ApplyAsync()
    {
        if (_currentSource == null && !ValidateRootFolder()) return;

        var source = ResolveBatchSource();
        var versions = BuildVersionResolver();
        var ruleSet = BuildRuleSet();

        IsBusy = true;
        StatusMessage = "Applying changes...";
        _cts = new CancellationTokenSource();
        var progress = new Progress<EditProgress>(p =>
        {
            ProgressCompleted = p.Completed;
            ProgressTotal = p.Total;
        });

        try
        {
            var changeSets = await _editExecutor.ApplyAsync(source, versions, ruleSet, CreateBackup, BackupFolder, progress, MaxDegreeOfParallelism, _cts.Token);
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

    /// <summary>The Asset column only earns its place once results span more than one asset (e.g. a batch Search) - a single opened export's rows are all the same asset, so it'd just be repeated noise.</summary>
    private void UpdateShowAssetColumn() =>
        ShowAssetColumn = SearchResults.Select(r => r.AssetPath).Distinct().Count() > 1;

    private bool EnsureWorkspace()
    {
        if (_workspace != null && !IsPakBacked && _workspaceRootFolder == RootFolder) return true;

        if (!ConfirmDiscardDirtyEdits()) return false;

        _workspace?.CloseAll();
        DisposeCurrentSource();
        _dirtyAssetPaths.Clear();
        SearchResults.Clear();
        _lastSearchQuery = null;
        _lastOpenedExport = null;

        var source = new LooseFolderAssetSource(RootFolder);
        _currentSource = source;
        _workspace = new AssetWorkspace(source, BuildVersionResolver());
        _workspaceRootFolder = RootFolder;
        IsPakBacked = false;
        return true;
    }

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

    /// <summary>The source batch Preview/Apply should run against: whatever's currently open (folder or pak), or a fresh loose-folder source over <see cref="RootFolder"/> if nothing has been explicitly opened yet.</summary>
    private IAssetSource ResolveBatchSource() => _currentSource ?? new LooseFolderAssetSource(RootFolder);

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

    private bool ValidateRootFolder()
    {
        if (Directory.Exists(RootFolder)) return true;
        StatusMessage = "Select a valid asset folder first.";
        return false;
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
        RootFolder = RootFolder,
        DefaultEngineVersion = DefaultEngineVersion,
        UsmapPath = UsmapPath,
        CreateBackup = CreateBackup,
        BackupFolder = BackupFolder,
        Scope = BuildScope(),
        Rules = Rules.Select(r => r.Rule).ToList(),
    };

    private void ApplySession(EditorSession session)
    {
        RootFolder = session.RootFolder;
        DefaultEngineVersion = session.DefaultEngineVersion;
        UsmapPath = session.UsmapPath;
        CreateBackup = session.CreateBackup;
        BackupFolder = session.BackupFolder;

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
