using System.Text;
using UAssetAPI;
using UAssetEditor.Core.AssetSources.PakWorker;

namespace UAssetEditor.Core.Tests;

/// <summary>
/// A copy of the worker in a folder of its own, so nothing beside it (an old oo2core_9_win64.dll left in a build output,
/// which repak_bind would load from beside the executable) can reach a test.
/// </summary>
public sealed class CleanWorkerFolder : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Worker_" + Guid.NewGuid().ToString("N"));

    public CleanWorkerFolder()
    {
        var source = PakWorkerProcess.ResolveDefaultWorkerExecutable();
        var sourceFolder = Path.GetDirectoryName(source)!;
        foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).StartsWith("oo2core_", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(_folder, Path.GetRelativePath(sourceFolder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        WorkerExecutable = Path.Combine(_folder, Path.GetFileName(source));
    }

    public string WorkerExecutable { get; }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}

/// <summary>
/// The Oodle DLL a worker starts with, and how a read that needs one is retried: through real workers, but never a real
/// DLL. Every DLL named here is absent, so a read that needs Oodle always fails and the failure text shows which path the
/// worker was given.
/// </summary>
[Collection("Pak")]
public sealed class PakWorkerOodleTests : IClassFixture<CleanWorkerFolder>, IDisposable
{
    private readonly CleanWorkerFolder _folder;
    private readonly PakWorkerProcess _worker;
    private readonly string? _savedVariable = Environment.GetEnvironmentVariable(OodleLibrary.EnvironmentVariable);

    public PakWorkerOodleTests(CleanWorkerFolder folder)
    {
        _folder = folder;
        Environment.SetEnvironmentVariable(OodleLibrary.EnvironmentVariable, null);
        _worker = new PakWorkerProcess(_folder.WorkerExecutable);
    }

    public void Dispose()
    {
        _worker.Dispose();
        Environment.SetEnvironmentVariable(OodleLibrary.EnvironmentVariable, _savedVariable);
    }

    private static string AbsentDll(string name) =>
        Path.Combine(Path.GetTempPath(), "UAssetEditorTest_NoOodle_" + Guid.NewGuid().ToString("N"), name);

    /// <summary>The worker's process id and the REPAK_OODLE_DLL it holds ("" when none), through the worker's test-only state entry.</summary>
    private async Task<(int ProcessId, string Variable)> StateAsync()
    {
        var request = new PakWorkerRequest { Opcode = PakWorkerOpcode.ReadEntry, EntryPath = "__TEST_STATE__" };
        var (response, payload) = await new PakWorkerClient(_worker).SendPooledAsync(request, cancellationToken: TestContext.Current.CancellationToken);
        using (payload)
        {
            Assert.True(response.Success);
            var parts = Encoding.UTF8.GetString(payload.Span).Split('|', 2);
            return (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), parts[1]);
        }
    }

    private static async Task<InvalidOperationException> ReadFailureAsync(PakReaderHandle reader)
    {
        await reader.OpenAsync(TestContext.Current.CancellationToken);
        return await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadEntryAsync("Foo.uasset", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Worker_WhenTheVariableIsRelative_IsStartedWithoutIt()
    {
        Environment.SetEnvironmentVariable(OodleLibrary.EnvironmentVariable, "oo2core_9_win64.dll");

        var (_, variable) = await StateAsync();

        Assert.Equal("", variable);
        Assert.Null(_worker.OodleLibraryPath);
    }

    [Fact]
    public async Task Worker_WhenTheVariableIsMisnamed_IsStartedWithoutIt()
    {
        var file = AbsentDll("payload.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, [0], TestContext.Current.CancellationToken);
        try
        {
            Environment.SetEnvironmentVariable(OodleLibrary.EnvironmentVariable, file);

            Assert.Equal("", (await StateAsync()).Variable);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public async Task Worker_WhenTheVariableNamesAnExistingDll_IsStartedWithThatPath()
    {
        var file = AbsentDll("oo2core_9_win64.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, [0], TestContext.Current.CancellationToken);
        try
        {
            Environment.SetEnvironmentVariable(OodleLibrary.EnvironmentVariable, file);

            Assert.Equal(file, (await StateAsync()).Variable);
            Assert.Equal(file, _worker.OodleLibraryPath);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public async Task ReadEntry_NeedingOodleWithNoDllFound_SaysWhatIsMissing()
    {
        using var pak = new TempPak(OodlePaks.CreateOodleMarked());
        using var reader = new PakReaderHandle(_worker, pak.Path, null, _ => OodleLibrary.NotFound);

        var ex = await ReadFailureAsync(reader);

        Assert.Equal("Oodle DLL not found. Point REPAK_OODLE_DLL at the game's oo2core_*_win64.dll.", ex.Message);
    }

    [Fact]
    public async Task ReadEntry_ForAPakWhoseDllIsOtherThanTheWorkers_RestartsTheWorkerWithIt()
    {
        var first = AbsentDll("oo2core_9_win64.dll");
        var second = AbsentDll("oo2core_7_win64.dll");
        using var pakA = new TempPak(OodlePaks.CreateOodleMarked());
        using var pakB = new TempPak(OodlePaks.CreateOodleMarked());

        using (var readerA = new PakReaderHandle(_worker, pakA.Path, null, _ => new OodleLookup(first)))
        {
            Assert.Equal($"Could not load {first}. Use a 64-bit oo2core_*_win64.dll.", (await ReadFailureAsync(readerA)).Message);
        }

        var afterFirst = await StateAsync();
        Assert.Equal(first, afterFirst.Variable);

        using (var readerB = new PakReaderHandle(_worker, pakB.Path, null, _ => new OodleLookup(second)))
        {
            Assert.Equal($"Could not load {second}. Use a 64-bit oo2core_*_win64.dll.", (await ReadFailureAsync(readerB)).Message);
        }

        var afterSecond = await StateAsync();
        Assert.Equal(second, afterSecond.Variable);
        Assert.NotEqual(afterFirst.ProcessId, afterSecond.ProcessId);
    }

    [Fact]
    public async Task ReadEntry_ForTheDllTheWorkerAlreadyHolds_DoesNotRestartIt()
    {
        var dll = AbsentDll("oo2core_9_win64.dll");
        using var pak = new TempPak(OodlePaks.CreateOodleMarked());
        using var reader = new PakReaderHandle(_worker, pak.Path, null, _ => new OodleLookup(dll));
        await ReadFailureAsync(reader);
        var before = await StateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadEntryAsync("Foo.uasset", TestContext.Current.CancellationToken));

        Assert.Equal(before.ProcessId, (await StateAsync()).ProcessId);
    }

    [Fact]
    public async Task ReadEntry_AfterTheWorkerWasRestartedForAnotherReader_StillWorks()
    {
        using var plain = TestPaks.CreateTemp(new Dictionary<string, byte[]> { ["Foo.uasset"] = Encoding.UTF8.GetBytes("foo-bytes") });
        using var oodle = new TempPak(OodlePaks.CreateOodleMarked());
        using var plainReader = new PakReaderHandle(_worker, plain.Path, null, _ => OodleLibrary.NotFound);
        await plainReader.OpenAsync(TestContext.Current.CancellationToken);
        using var oodleReader = new PakReaderHandle(_worker, oodle.Path, null, _ => new OodleLookup(AbsentDll("oo2core_9_win64.dll")));
        await ReadFailureAsync(oodleReader);

        using var bytes = await plainReader.ReadEntryAsync("Foo.uasset", TestContext.Current.CancellationToken);

        Assert.Equal("foo-bytes", Encoding.UTF8.GetString(bytes.Span));
    }

    [Fact]
    public async Task Open_AndReadingAnEntryThatNeedsNoOodle_NeverSearchesForADll()
    {
        var searches = 0;
        using var plain = TestPaks.CreateTemp(new Dictionary<string, byte[]> { ["Foo.uasset"] = Encoding.UTF8.GetBytes("foo-bytes") });
        using var reader = new PakReaderHandle(_worker, plain.Path, null, _ => { searches++; return OodleLibrary.NotFound; });

        await reader.OpenAsync(TestContext.Current.CancellationToken);
        using var bytes = await reader.ReadEntryAsync("Foo.uasset", TestContext.Current.CancellationToken);

        Assert.Equal(0, searches);
    }

    [Fact]
    public async Task ReadEntry_NeedingOodle_SearchesForADllOnceForTheFailedRead()
    {
        var searches = 0;
        using var pak = new TempPak(OodlePaks.CreateOodleMarked());
        using var reader = new PakReaderHandle(_worker, pak.Path, null, _ => { searches++; return OodleLibrary.NotFound; });

        await ReadFailureAsync(reader);

        Assert.Equal(1, searches);
    }
}

/// <summary>Oodle-marked test paks: repak cannot write Oodle, so one is a Zlib pak whose footer names Oodle instead.</summary>
internal static class OodlePaks
{
    public static string CreateOodleMarked()
    {
        var path = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_" + Guid.NewGuid() + ".pak");
        using (var stream = File.Create(path))
        using (var builder = new PakBuilder())
        {
            builder.Compression([PakCompression.Zlib]);
            using var writer = builder.Writer(stream, PakVersion.V11, "../../../Game/", 0);
            writer.WriteFile("Foo.uasset", new byte[4096]);
            writer.WriteIndex();
        }

        var bytes = File.ReadAllBytes(path);
        var footer = bytes.AsSpan(bytes.Length - 512);
        var at = footer.IndexOf("Zlib"u8);
        Assert.True(at >= 0, "the pak footer does not name its Zlib codec");
        "Oodle"u8.CopyTo(footer[at..]);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
