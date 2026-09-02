using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.AssetSources.IoStore;

namespace UAssetEditor.App.ViewModels;

/// <summary>What <see cref="ConvertIoStoreToLegacyViewModel.RunAsync"/> converts the source .utoc into - see <see cref="ConvertIoStoreToLegacyViewModel.OutputFormat"/>.</summary>
public enum LegacyOutputFormat
{
    Folder,
    Pak,
}

/// <summary>One selectable entry in the Convert IoStore to Legacy dialog's output-format dropdown.</summary>
public sealed record LegacyOutputFormatOption(LegacyOutputFormat Value, string Label);

/// <summary>
/// Backs the Convert IoStore to Legacy dialog - converts every entry of a chosen .utoc container
/// into legacy format via retoc's to-legacy (<see cref="RetocProcess.ConvertToLegacyAsync"/>),
/// into either a loose folder or a fresh .pak directly (retoc's own OUTPUT argument accepts
/// either), off the UI thread. Unlike <see cref="MainViewModel.ConvertSelectedCommand"/> (which
/// only converts checked entries of an already-browsed container into the app's own workspace),
/// this is a standalone one-shot conversion of a whole container to a file on disk, mirroring
/// how <see cref="UnpackPakViewModel"/> is a standalone counterpart to browsing a pak's tree.
/// </summary>
public sealed partial class ConvertIoStoreToLegacyViewModel : ObservableObject, IDisposable
{
    [ObservableProperty] private string _sourceUtocPath;
    [ObservableProperty] private string _outputPath = "";
    [ObservableProperty] private LegacyOutputFormat _outputFormat = LegacyOutputFormat.Folder;
    [ObservableProperty] private string _aesKeyHex;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private string _status = "";

    private CancellationTokenSource? _cts;

    public IReadOnlyList<LegacyOutputFormatOption> OutputFormats { get; } =
        [new LegacyOutputFormatOption(LegacyOutputFormat.Folder, "Loose folder"), new LegacyOutputFormatOption(LegacyOutputFormat.Pak, "Legacy .pak")];

    public bool IsNotRunning => !IsRunning;

    /// <summary>retoc gives no incremental progress for to-legacy, so the progress bar just spins instead of tracking real completion.</summary>
    public bool IsIndeterminateProgress => IsRunning;

    public ConvertIoStoreToLegacyViewModel(string? initialSourceUtocPath, string initialAesKeyHex)
    {
        _sourceUtocPath = initialSourceUtocPath ?? "";
        _aesKeyHex = initialAesKeyHex;
    }

    [RelayCommand]
    private void BrowseSource()
    {
        var dialog = new OpenFileDialog { Title = "Select .utoc to convert", Filter = "IoStore container (*.utoc)|*.utoc" };
        if (dialog.ShowDialog() == true) SourceUtocPath = dialog.FileName;
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        if (OutputFormat == LegacyOutputFormat.Pak)
        {
            var dialog = new SaveFileDialog { Title = "Convert to...", Filter = "Pak files (*.pak)|*.pak" };
            if (dialog.ShowDialog() == true) OutputPath = dialog.FileName;
        }
        else
        {
            var dialog = new OpenFolderDialog { Title = "Select destination folder" };
            if (dialog.ShowDialog() == true) OutputPath = dialog.FolderName;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        Status = "Converting to legacy format...";
        _cts = new CancellationTokenSource();
        try
        {
            var aesKey = PakAesKey.Parse(AesKeyHex);
            await RetocProcess.ConvertToLegacyAsync(SourceUtocPath, OutputPath, filters: [], aesKey, _cts.Token);
            Status = $"Converted to {OutputPath}.";
            IsDone = true;
        }
        catch (OperationCanceledException)
        {
            Status = "Canceled.";
        }
        catch (Exception ex)
        {
            Status = $"Convert failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool CanRun() => !IsRunning && !string.IsNullOrWhiteSpace(SourceUtocPath) && !string.IsNullOrWhiteSpace(OutputPath);

    partial void OnSourceUtocPathChanged(string value) => RunCommand.NotifyCanExecuteChanged();
    partial void OnOutputPathChanged(string value) => RunCommand.NotifyCanExecuteChanged();

    partial void OnOutputFormatChanged(LegacyOutputFormat value) => OutputPath = "";

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsNotRunning));
        OnPropertyChanged(nameof(IsIndeterminateProgress));
    }
}
