using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.App.ViewModels;

/// <summary>Backs the Unpack .pak dialog - extracts every entry of a chosen .pak to a chosen folder via <see cref="PakUnpacker"/>, off the UI thread, with live progress.</summary>
public sealed partial class UnpackPakViewModel : ObservableObject, IDisposable
{
    [ObservableProperty] private string _sourcePakPath;
    [ObservableProperty] private string _destinationFolder = "";
    [ObservableProperty] private string _aesKeyHex;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private int _progressDone;
    [ObservableProperty] private int _progressTotal = 1;
    [ObservableProperty] private string _status = "";

    private CancellationTokenSource? _cts;

    public UnpackPakViewModel(string? initialSourcePakPath, string initialAesKeyHex)
    {
        _sourcePakPath = initialSourcePakPath ?? "";
        _aesKeyHex = initialAesKeyHex;
    }

    [RelayCommand]
    private void BrowseSource()
    {
        var dialog = new OpenFileDialog { Title = "Select .pak to unpack", Filter = "Pak files (*.pak)|*.pak" };
        if (dialog.ShowDialog() == true) SourcePakPath = dialog.FileName;
    }

    [RelayCommand]
    private void BrowseDestination()
    {
        var dialog = new OpenFolderDialog { Title = "Select destination folder" };
        if (dialog.ShowDialog() == true) DestinationFolder = dialog.FolderName;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        Status = "Unpacking...";
        _cts = new CancellationTokenSource();
        try
        {
            var aesKey = PakAesKey.Parse(AesKeyHex);
            var sourcePath = SourcePakPath;
            var destination = DestinationFolder;
            var progress = new Progress<(int Done, int Total)>(p => { ProgressDone = p.Done; ProgressTotal = p.Total; });

            var result = await Task.Run(() =>
            {
                using var source = new PakAssetSource(sourcePath, aesKey);
                ProgressTotal = source.ListAllEntries().Count;
                return PakUnpacker.Unpack(source, destination, progress: progress, cancellationToken: _cts.Token);
            }, _cts.Token);

            Status = result.HasFailures
                ? $"Unpacked {result.SucceededCount} file(s) to {destination} - {result.FailedEntries.Count} failed."
                : $"Unpacked {result.SucceededCount} file(s) to {destination}.";
            IsDone = true;
        }
        catch (OperationCanceledException)
        {
            Status = "Canceled.";
        }
        catch (Exception ex)
        {
            Status = $"Unpack failed: {ex.Message}";
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

    private bool CanRun() => !IsRunning && !string.IsNullOrWhiteSpace(SourcePakPath) && !string.IsNullOrWhiteSpace(DestinationFolder);

    partial void OnSourcePakPathChanged(string value) => RunCommand.NotifyCanExecuteChanged();
    partial void OnDestinationFolderChanged(string value) => RunCommand.NotifyCanExecuteChanged();

    public bool IsNotRunning => !IsRunning;

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsNotRunning));
    }
}
