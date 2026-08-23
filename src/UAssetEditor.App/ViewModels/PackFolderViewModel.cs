using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using UAssetAPI;
using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.App.ViewModels;

/// <summary>One selectable entry in the Pack dialog's compression dropdown - "Default" (null) lets repak pick its own, the same behavior the existing Repack flow already uses.</summary>
public sealed record CompressionOption(PakCompression? Value, string Label);

/// <summary>Backs the Pack Folder into .pak dialog - builds a brand-new .pak from a chosen loose folder via <see cref="PakPacker"/>, off the UI thread, with live progress.</summary>
public sealed partial class PackFolderViewModel : ObservableObject
{
    [ObservableProperty] private string _sourceFolder;
    [ObservableProperty] private string _outputPakPath = "";
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

    public IReadOnlyList<PakVersion> Versions { get; } = Enum.GetValues<PakVersion>();

    public IReadOnlyList<CompressionOption> Compressions { get; } =
        [new CompressionOption(null, "Default"), ..Enum.GetValues<PakCompression>().Select(c => new CompressionOption(c, c.ToString()))];

    public bool IsNotRunning => !IsRunning;

    public PackFolderViewModel(string? initialSourceFolder, string initialMountPoint, string initialAesKeyHex)
    {
        _sourceFolder = initialSourceFolder ?? "";
        _mountPoint = initialMountPoint;
        _aesKeyHex = initialAesKeyHex;
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
        var dialog = new SaveFileDialog { Title = "Pack to...", Filter = "Pak files (*.pak)|*.pak" };
        if (dialog.ShowDialog() == true) OutputPakPath = dialog.FileName;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        Status = "Packing...";
        _cts = new CancellationTokenSource();
        try
        {
            var aesKey = PakAesKey.Parse(AesKeyHex);
            var sourceFolder = SourceFolder;
            var outputPath = OutputPakPath;
            var mountPoint = MountPoint;
            var version = Version;
            var compression = Compression is { } c ? new[] { c } : null;
            var progress = new Progress<(int Done, int Total)>(p => { ProgressDone = p.Done; ProgressTotal = p.Total; });

            await Task.Run(() => PakPacker.Build(sourceFolder, outputPath, mountPoint, version, compression, aesKey, progress, _cts.Token), _cts.Token);

            Status = $"Packed {ProgressDone} file(s) to {outputPath}.";
            IsDone = true;
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
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    private bool CanRun() => !IsRunning && !string.IsNullOrWhiteSpace(SourceFolder) && !string.IsNullOrWhiteSpace(OutputPakPath);

    partial void OnSourceFolderChanged(string value) => RunCommand.NotifyCanExecuteChanged();
    partial void OnOutputPakPathChanged(string value) => RunCommand.NotifyCanExecuteChanged();

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsNotRunning));
    }
}
