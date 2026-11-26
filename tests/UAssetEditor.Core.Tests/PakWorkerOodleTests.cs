using System.Text;
using UAssetAPI;
using UAssetEditor.Core.AssetSources.PakWorker;

[assembly: AssemblyFixture(typeof(UAssetEditor.Core.Tests.PakWorkerTestHooks))]

namespace UAssetEditor.Core.Tests;

/// <summary>Lets this test process's workers answer the test-only entries (crash, state); production workers never do.</summary>
public sealed class PakWorkerTestHooks
{
    public PakWorkerTestHooks() =>
        Environment.SetEnvironmentVariable(PakWorkerProcess.TestHooksEnvironmentVariable, "1");
}

/// <summary>
/// A copy of the worker in a folder of its own, so nothing beside it (an oo2core_9_win64.dll left in a build output) can
/// reach a test, whatever the native library does.
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

    /// <summary>Deletes the folder, and throws while a worker still holds a file in it; a second call, after one that succeeded, does nothing.</summary>
    public void Delete()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>
    /// Deletes the folder where it can. It never throws: it runs while a failed test unwinds, and an error here would
    /// replace that test's own failure. A test that needs the folder gone calls <see cref="Delete"/>.
    /// </summary>
    public void Dispose()
    {
        try
        {
            Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TestContext.Current.SendDiagnosticMessage($"Could not delete the worker folder {_folder}: {ex.Message}");
        }
    }
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

    /// <summary>The process id and the REPAK_OODLE_DLL ("" when none) of <paramref name="worker"/>, through its test-only state entry.</summary>
    internal static async Task<(int ProcessId, string Variable)> StateAsync(PakWorkerProcess worker)
    {
        var request = new PakWorkerRequest { Opcode = PakWorkerOpcode.ReadEntry, EntryPath = "__TEST_STATE__" };
        var (response, payload) = await new PakWorkerClient(worker).SendPooledAsync(request, cancellationToken: TestContext.Current.CancellationToken);
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

    private static string CouldNotLoad(string dll) => $"Could not load {dll}. Use a 64-bit oo2core_*_win64.dll.";

    [Fact]
    public async Task Worker_WhenTheVariableIsRelative_IsStartedWithoutIt()
    {
        Environment.SetEnvironmentVariable(OodleLibrary.EnvironmentVariable, "oo2core_9_win64.dll");

        var (_, variable) = await StateAsync(_worker);

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

            Assert.Equal("", (await StateAsync(_worker)).Variable);
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

            Assert.Equal(file, (await StateAsync(_worker)).Variable);
            Assert.Equal(file, _worker.OodleLibraryPath);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Fact]
    public async Task Worker_WithoutTheTestHooksVariable_IgnoresTheTestOnlyEntries()
    {
        var saved = Environment.GetEnvironmentVariable(PakWorkerProcess.TestHooksEnvironmentVariable);
        Environment.SetEnvironmentVariable(PakWorkerProcess.TestHooksEnvironmentVariable, null);
        try
        {
            var request = new PakWorkerRequest { Opcode = PakWorkerOpcode.ReadEntry, EntryPath = "__TEST_STATE__" };
            var (response, payload) = await new PakWorkerClient(_worker).SendPooledAsync(request, cancellationToken: TestContext.Current.CancellationToken);
            payload.Dispose();

            Assert.False(response.Success);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PakWorkerProcess.TestHooksEnvironmentVariable, saved);
        }
    }

    [Fact]
    public async Task Ensure_WhenTheWorkerCannotStart_LeavesTheProcessDeadAndFailsAgainNextTime()
    {
        using var broken = new PakWorkerProcess(Path.Combine(Path.GetTempPath(), "UAssetEditorTest_NoWorker_" + Guid.NewGuid().ToString("N") + ".exe"));

        await Assert.ThrowsAnyAsync<Exception>(() => broken.EnsureAsync(TestContext.Current.CancellationToken));

        Assert.True(broken.IsDead);
        Assert.Null(broken.OodleLibraryPath);
        await Assert.ThrowsAnyAsync<Exception>(() => broken.EnsureAsync(TestContext.Current.CancellationToken));
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
    public async Task ReadEntry_ForAPakWhoseDllIsOtherThanTheWorkers_ReadsOnAWorkerOfItsOwn()
    {
        var dll = AbsentDll("oo2core_9_win64.dll");
        using var pak = new TempPak(OodlePaks.CreateOodleMarked());
        var before = await StateAsync(_worker);

        using (var reader = new PakReaderHandle(_worker, pak.Path, null, _ => new OodleLookup(dll)))
        {
            Assert.Equal(CouldNotLoad(dll), (await ReadFailureAsync(reader)).Message);
        }

        var own = await StateAsync(_worker.ForOodleLibrary(dll));
        var after = await StateAsync(_worker);
        Assert.Equal(dll, own.Variable);
        Assert.Equal("", after.Variable);
        Assert.Equal(before.ProcessId, after.ProcessId);
        Assert.NotEqual(after.ProcessId, own.ProcessId);
    }

    [Fact]
    public async Task ReadEntry_ForAnotherDll_UsesAnotherWorkerAndLeavesTheFirstOnesAlone()
    {
        var first = AbsentDll("oo2core_9_win64.dll");
        var second = AbsentDll("oo2core_7_win64.dll");
        using var pakA = new TempPak(OodlePaks.CreateOodleMarked());
        using var pakB = new TempPak(OodlePaks.CreateOodleMarked());
        using (var readerA = new PakReaderHandle(_worker, pakA.Path, null, _ => new OodleLookup(first)))
        {
            Assert.Equal(CouldNotLoad(first), (await ReadFailureAsync(readerA)).Message);
        }

        var firstBefore = await StateAsync(_worker.ForOodleLibrary(first));
        using (var readerB = new PakReaderHandle(_worker, pakB.Path, null, _ => new OodleLookup(second)))
        {
            Assert.Equal(CouldNotLoad(second), (await ReadFailureAsync(readerB)).Message);
        }

        var secondState = await StateAsync(_worker.ForOodleLibrary(second));
        Assert.Equal(second, secondState.Variable);
        Assert.Equal(firstBefore, await StateAsync(_worker.ForOodleLibrary(first)));
    }

    [Fact]
    public async Task ReadEntry_ForTheDllItsWorkerWasStartedWith_DoesNotStartAnotherWorker()
    {
        var dll = AbsentDll("oo2core_9_win64.dll");
        using var pak = new TempPak(OodlePaks.CreateOodleMarked());
        using var reader = new PakReaderHandle(_worker, pak.Path, null, _ => new OodleLookup(dll));
        await ReadFailureAsync(reader);
        var before = await StateAsync(_worker.ForOodleLibrary(dll));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadEntryAsync("Foo.uasset", TestContext.Current.CancellationToken));

        Assert.Equal(CouldNotLoad(dll), ex.Message);
        Assert.Equal(before, await StateAsync(_worker.ForOodleLibrary(dll)));
    }

    [Fact]
    public async Task ReadEntry_ForAnOodleReaderThatMoves_LeavesEveryOtherSessionOnTheDefaultWorkerWorking()
    {
        using var plain = TestPaks.CreateTemp(new Dictionary<string, byte[]> { ["Foo.uasset"] = Encoding.UTF8.GetBytes("foo-bytes") });
        using var oodle = new TempPak(OodlePaks.CreateOodleMarked());
        var written = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Written_" + Guid.NewGuid().ToString("N") + ".pak");
        try
        {
            using var plainReader = new PakReaderHandle(_worker, plain.Path, null, _ => OodleLibrary.NotFound);
            await plainReader.OpenAsync(TestContext.Current.CancellationToken);
            using var writer = new PakWriterHandle(_worker);
            await writer.OpenAsync(written, "../../../Game/", PakVersion.V11, null, null, cancellationToken: TestContext.Current.CancellationToken);
            await writer.WriteFileAsync("Bar.uasset", Encoding.UTF8.GetBytes("bar"), TestContext.Current.CancellationToken);
            var before = await StateAsync(_worker);

            using (var oodleReader = new PakReaderHandle(_worker, oodle.Path, null, _ => new OodleLookup(AbsentDll("oo2core_9_win64.dll"))))
            {
                await ReadFailureAsync(oodleReader);
            }

            using var bytes = await plainReader.ReadEntryAsync("Foo.uasset", TestContext.Current.CancellationToken);
            Assert.Equal("foo-bytes", Encoding.UTF8.GetString(bytes.Span));
            await writer.WriteFileAsync("Baz.uasset", Encoding.UTF8.GetBytes("baz"), TestContext.Current.CancellationToken);
            await writer.WriteIndexAsync(TestContext.Current.CancellationToken);
            Assert.Equal(before, await StateAsync(_worker));
        }
        finally
        {
            TempPak.DeleteQuietly(written);
        }
    }

    [Fact]
    public async Task Worker_AfterACrash_IsRestartedWithTheCurrentVariableNotAnEarlierFailedPath()
    {
        var failed = AbsentDll("oo2core_9_win64.dll");
        var valid = AbsentDll("oo2core_8_win64.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(valid)!);
        await File.WriteAllBytesAsync(valid, [0], TestContext.Current.CancellationToken);
        try
        {
            using var pak = new TempPak(OodlePaks.CreateOodleMarked());
            using (var reader = new PakReaderHandle(_worker, pak.Path, null, _ => new OodleLookup(failed)))
            {
                await ReadFailureAsync(reader);
            }

            Environment.SetEnvironmentVariable(OodleLibrary.EnvironmentVariable, valid);
            var crash = new PakWorkerRequest { Opcode = PakWorkerOpcode.ReadEntry, EntryPath = "__TEST_CRASH__" };
            await Assert.ThrowsAsync<PakWorkerCrashedException>(() => new PakWorkerClient(_worker).SendPooledAsync(crash, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(valid, (await StateAsync(_worker)).Variable);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(valid)!, recursive: true);
        }
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
    public async Task ReadEntry_NeedingOodleWithNoDllFound_SearchesAgainOnEachRead()
    {
        var searches = 0;
        using var pak = new TempPak(OodlePaks.CreateOodleMarked());
        using var reader = new PakReaderHandle(_worker, pak.Path, null, _ => { searches++; return OodleLibrary.NotFound; });

        await ReadFailureAsync(reader);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadEntryAsync("Foo.uasset", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadEntryAsync("Foo.uasset", TestContext.Current.CancellationToken));

        Assert.Equal(3, searches);
    }

    [Fact]
    public async Task ReadEntry_AfterAFailedSearch_FindsADllThatAppearedMeanwhile()
    {
        var dll = AbsentDll("oo2core_9_win64.dll");
        var present = false;
        using var pak = new TempPak(OodlePaks.CreateOodleMarked());
        using var reader = new PakReaderHandle(_worker, pak.Path, null, _ => present ? new OodleLookup(dll) : OodleLibrary.NotFound);

        var first = await ReadFailureAsync(reader);
        present = true;
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadEntryAsync("Foo.uasset", TestContext.Current.CancellationToken));

        Assert.StartsWith("Oodle DLL not found", first.Message, StringComparison.Ordinal);
        Assert.Equal(CouldNotLoad(dll), second.Message);
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
