using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.AssetSources.IoStore;

namespace UAssetEditor.App.ViewModels;

/// <summary>One selectable entry in the Pack dialog's compression dropdown - "Default" (null) lets repak pick its own, the same behavior the existing Repack flow already uses.</summary>
public sealed record CompressionOption(PakCompression? Value, string Label);

/// <summary>What <see cref="PackFolderViewModel.RunAsync"/> builds the source folder into - see <see cref="PackFolderViewModel.OutputFormat"/>.</summary>
public enum PackOutputFormat
{
    Pak,
    IoStore,
}

/// <summary>One selectable entry in the Pack dialog's output-format dropdown.</summary>
public sealed record PackOutputFormatOption(PackOutputFormat Value, string Label);

/// <summary>
/// Backs the Pack Folder dialog - builds a brand-new .pak from a chosen loose folder via
/// <see cref="PakPacker"/>, or converts that same folder straight into an IoStore .utoc/.ucas
/// pair via retoc's to-zen (<see cref="RetocProcess.ConvertToZenAsync"/>), off the UI thread,
/// with live progress where the underlying tool provides any.
/// </summary>
public sealed partial class PackFolderViewModel : ObservableObject, IDisposable
{
    private readonly EngineVersion _defaultEngineVersion;

    [ObservableProperty] private string _sourceFolder;
    [ObservableProperty] private string _outputPath = "";
    [ObservableProperty] private PackOutputFormat _outputFormat = PackOutputFormat.Pak;
    [ObservableProperty] private string _mountPoint;
    [ObservableProperty] private string _aesKeyHex;
    [ObservableProperty] private PakVersion _version = PakVersion.V11;
    [ObservableProperty] private PakCompression? _compression;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private int _progressDone;
    [ObservableProperty] private int _progressTotal = 1;
    [ObservableProperty] private string _status = "";

    private CancellationTokenSource? _cts;

    /// <summary>
    /// Whether <see cref="MountPoint"/> holds a value that shouldn't be silently overwritten
    /// by the auto-guess in <see cref="OnSourceFolderChanged"/> - true once it came from
    /// something authoritative (an already-open pak, or the user typing/reading one in
    /// directly), false while it's still just a placeholder default. See that method for why
    /// a folder-name guess is the right default but must never clobber a real value.
    /// </summary>
    private bool _mountPointIsAuthoritative;
    private bool _suppressMountPointTracking;

    public IReadOnlyList<PakVersion> Versions { get; } = Enum.GetValues<PakVersion>();

    public IReadOnlyList<CompressionOption> Compressions { get; } =
        [new CompressionOption(null, "Default"), ..Enum.GetValues<PakCompression>().Select(c => new CompressionOption(c, c.ToString()))];

    public IReadOnlyList<PackOutputFormatOption> OutputFormats { get; } =
        [new PackOutputFormatOption(PackOutputFormat.Pak, "Legacy .pak"), new PackOutputFormatOption(PackOutputFormat.IoStore, "IoStore (.utoc)")];

    public bool IsNotRunning => !IsRunning;
    public bool IsPakFormat => OutputFormat == PackOutputFormat.Pak;
    public bool IsIoStoreFormat => OutputFormat == PackOutputFormat.IoStore;

    /// <summary>Retoc gives no incremental progress for to-zen, unlike <see cref="PakPacker"/>'s per-file callback - so the progress bar just spins while an IoStore pack is running instead of tracking real completion.</summary>
    public bool IsIndeterminateProgress => IsRunning && IsIoStoreFormat;

    public PackFolderViewModel(string? initialSourceFolder, string initialMountPoint, string initialAesKeyHex, EngineVersion defaultEngineVersion,
        bool mountPointIsAuthoritative = false, PakVersion? initialVersion = null)
    {
        _sourceFolder = initialSourceFolder ?? "";
        _mountPoint = initialMountPoint;
        _aesKeyHex = initialAesKeyHex;
        _defaultEngineVersion = defaultEngineVersion;
        _mountPointIsAuthoritative = mountPointIsAuthoritative;
        if (initialVersion is { } version) _version = version;
    }

    [RelayCommand]
    private void BrowseSource()
    {
        var dialog = new OpenFolderDialog { Title = "Select folder to pack" };
        if (dialog.ShowDialog() == true) SourceFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        var dialog = IsIoStoreFormat
            ? new SaveFileDialog { Title = "Pack to...", Filter = "IoStore container (*.utoc)|*.utoc" }
            : new SaveFileDialog { Title = "Pack to...", Filter = "Pak files (*.pak)|*.pak" };
        if (dialog.ShowDialog() == true) OutputPath = dialog.FileName;
    }

    /// <summary>
    /// There's no way to derive a game's mount point OR its pak format version from its name
    /// or folder layout alone - both vary per game (e.g. Days Gone's mount point is
    /// "../../../BendGame/", not the generic "../../../Game/" default, and building a new
    /// pak at the wrong version is exactly the kind of mistake that looks structurally fine
    /// in this tool's own inspector while the actual game silently refuses to recognize the
    /// file) and this tool has no built-in game database for either. The one reliable source
    /// of truth is an actual existing pak from that game, so this reads both real header
    /// values straight from one the user points at - no need to have already opened it as
    /// the app's main workspace source first.
    /// </summary>
    [RelayCommand]
    private void DetectMountPoint()
    {
        var dialog = new OpenFileDialog { Title = "Read mount point and version from an existing .pak...", Filter = "Pak files (*.pak)|*.pak" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var header = PakMountPointReader.Read(dialog.FileName, PakAesKey.Parse(AesKeyHex));
            MountPoint = header.MountPoint;
            Version = header.Version;
            Status = $"Mount point and version ({header.Version}) read from '{System.IO.Path.GetFileName(dialog.FileName)}'.";
        }
        catch (Exception ex)
        {
            Status = $"Failed to read mount point: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        _cts = new CancellationTokenSource();
        try
        {
            if (IsIoStoreFormat)
                await RunIoStoreAsync(_cts.Token);
            else
                await RunPakAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = "Canceled.";
        }
        catch (Exception ex)
        {
            Status = $"Pack failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task RunPakAsync(CancellationToken cancellationToken)
    {
        Status = "Packing...";
        var aesKey = PakAesKey.Parse(AesKeyHex);
        var sourceFolder = SourceFolder;
        var outputPath = OutputPath;
        var mountPoint = MountPoint;
        var version = Version;
        var compression = Compression is { } c ? new[] { c } : null;
        var progress = new Progress<(int Done, int Total)>(p => { ProgressDone = p.Done; ProgressTotal = p.Total; });

        var result = await Task.Run(() => PakPacker.Build(sourceFolder, outputPath, mountPoint, version, compression, aesKey, progress, cancellationToken), cancellationToken);

        Status = result.HasFailures
            ? $"Pack failed: {result.FailedEntries[0].Reason}"
            : $"Packed {ProgressDone} file(s) to {outputPath}.";
        IsDone = true;
    }

    private async Task RunIoStoreAsync(CancellationToken cancellationToken)
    {
        var retocVersion = EngineVersionMapping.ToRetocVersion(_defaultEngineVersion);
        if (retocVersion == null)
        {
            Status = $"'{_defaultEngineVersion}' has no IoStore equivalent - pick a UE4.25+ engine version first.";
            return;
        }

        var retocInput = RetocDirectoryInputResolver.Resolve(SourceFolder);
        if (retocInput == null)
        {
            Status = "Pick a source folder that isn't a drive root.";
            return;
        }

        Status = "Packing to IoStore...";
        ProgressDone = 0;
        ProgressTotal = 1;

        var aesKey = PakAesKey.Parse(AesKeyHex);
        await RetocProcess.ConvertToZenAsync(retocInput, OutputPath, retocVersion, aesKey, cancellationToken);

        ProgressDone = 1;
        Status = $"Packed to {OutputPath}.";
        IsDone = true;
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool CanRun() => !IsRunning && !string.IsNullOrWhiteSpace(SourceFolder) && !string.IsNullOrWhiteSpace(OutputPath);

    /// <summary>
    /// Defaults the mount point to the chosen folder's own name ("../../../&lt;name&gt;/")
    /// whenever nothing more authoritative is already there. This is deliberately a guess,
    /// not a detection - there's no reliable structural signal to tell a real game project
    /// root apart from an arbitrary mod folder (Content is one convention among several -
    /// Config, Movies, Plugins etc. are just as legitimate, so keying off any one of them is
    /// no more trustworthy than the others). Naming the folder after the mount point is
    /// simply the convention this tool assumes by default; it's one edit away from being
    /// fixed by hand, or replaced wholesale via Detect Mount Point once a real pak is available.
    /// </summary>
    partial void OnSourceFolderChanged(string value)
    {
        RunCommand.NotifyCanExecuteChanged();

        if (_mountPointIsAuthoritative) return;

        var folderName = new System.IO.DirectoryInfo(value.TrimEnd('\\', '/')).Name;
        if (folderName.Length == 0) return;

        _suppressMountPointTracking = true;
        MountPoint = $"../../../{folderName}/";
        _suppressMountPointTracking = false;
    }

    partial void OnMountPointChanged(string value)
    {
        if (!_suppressMountPointTracking)
            _mountPointIsAuthoritative = true;
    }

    partial void OnOutputPathChanged(string value) => RunCommand.NotifyCanExecuteChanged();

    partial void OnOutputFormatChanged(PackOutputFormat value)
    {
        OnPropertyChanged(nameof(IsPakFormat));
        OnPropertyChanged(nameof(IsIoStoreFormat));
        OnPropertyChanged(nameof(IsIndeterminateProgress));
    }

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsNotRunning));
        OnPropertyChanged(nameof(IsIndeterminateProgress));
    }
}
