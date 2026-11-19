using UAssetAPI;
using UAssetEditor.Core.AssetSources.PakWorker;

namespace UAssetEditor.Core.Tests;

/// <summary>
/// A request that fails in the worker must not leave anything of it open there: the worker lives as long as the app, and
/// an open file handle held for a session nobody can name (the error reply has no session id) is never released.
/// </summary>
[Collection("Pak")]
public sealed class PakWorkerSessionTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files) TempPak.DeleteQuietly(file);
    }

    private string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_" + Guid.NewGuid() + ".pak");
        _files.Add(path);
        return path;
    }

    [Fact]
    public async Task AReaderThatFailsToOpen_DoesNotKeepTheFileOpenInTheWorker()
    {
        var notAPak = TempFile();
        await File.WriteAllBytesAsync(notAPak, new byte[4096], TestContext.Current.CancellationToken);
        using var worker = new PakWorkerProcess();
        using var reader = new PakReaderHandle(worker, notAPak, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.OpenAsync(TestContext.Current.CancellationToken));

        File.Delete(notAPak); // a sharing violation here means the worker still holds the file
        Assert.False(File.Exists(notAPak));
    }

    [Fact]
    public async Task AWriterThatFailsToOpen_DoesNotKeepTheOutputFileOpenInTheWorker()
    {
        var output = TempFile();
        using var worker = new PakWorkerProcess();
        using var writer = new PakWriterHandle(worker);

        // A NUL in the mount point is refused by the native writer after the output file was created.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.OpenAsync(output, "../../../a\0b/", PakVersion.V11, null, null, cancellationToken: TestContext.Current.CancellationToken));

        File.Delete(output);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void AFailureIsReportedWithEveryMessageOfItsChain_SoTheStreamsOwnErrorIsNotLost()
    {
        var failure = new InvalidOperationException("Failed to read the pak", new IOException("The device is not ready.", new EndOfStreamException("Unexpected end.")));

        Assert.Equal("Failed to read the pak: The device is not ready.: Unexpected end.", PakWorkerError.Describe(failure));
    }

    [Fact]
    public void AFailureWithoutAnInnerException_IsReportedAsItsMessage()
    {
        Assert.Equal("boom", PakWorkerError.Describe(new InvalidOperationException("boom")));
    }

    [Fact]
    public void AnInnerMessageTheOuterOneAlreadyQuotes_IsNotRepeated()
    {
        var failure = new InvalidOperationException("Failed: disk full", new IOException("disk full"));

        Assert.Equal("Failed: disk full", PakWorkerError.Describe(failure));
    }
}
