using UAssetEditor.Core.AssetSources.IoStore;

namespace UAssetEditor.Core.Tests;

/// <summary><see cref="RetocProcess.StagedAsync"/>, with a build that writes the container the way retoc does.</summary>
public sealed class RetocStagingTests : IDisposable
{
    private static readonly string[] ContainerExtensions = [".utoc", ".ucas", ".pak"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_RetocStaging_" + Guid.NewGuid());

    public RetocStagingTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Writes the whole container beside the .utoc it is given, then fails if told to.
    private static Func<string, Task> Build(string contents, Exception? failure = null, List<string>? outputs = null) =>
        async utoc =>
        {
            outputs?.Add(utoc);
            await WriteContainerAsync(utoc, contents);
            if (failure != null) throw failure;
        };

    private static async Task WriteContainerAsync(string utoc, string contents)
    {
        foreach (var extension in ContainerExtensions)
            await File.WriteAllTextAsync(Path.ChangeExtension(utoc, extension), contents + extension, TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, string> Contents(string folder) =>
        Directory.EnumerateFileSystemEntries(folder).ToDictionary(entry => Path.GetFileName(entry), entry => File.Exists(entry) ? File.ReadAllText(entry) : "<folder>");

    public static TheoryData<Exception> Failures => [new IOException("disk full"), new OperationCanceledException()];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailedOrCancelledPack_KeepsTheContainerItWasToReplace_AndLeavesNothingBehind(Exception failure)
    {
        var output = Path.Combine(_dir, "mod_P.utoc");
        await WriteContainerAsync(output, "old");

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => RetocProcess.StagedAsync(output, writesCompanionPak: true, Build("new", failure)));

        Assert.Same(failure, thrown);
        Assert.Equal(
            new Dictionary<string, string> { ["mod_P.utoc"] = "old.utoc", ["mod_P.ucas"] = "old.ucas", ["mod_P.pak"] = "old.pak" },
            Contents(_dir));
    }

    [Fact]
    public async Task APack_BuildsUnderTheOutputsOwnName_AndMovesTheWholeContainerIntoPlace()
    {
        // The .utoc's name sets the container ID (Marvel Rivals' <mod>_9999999_P), so it must not be built under another name.
        var output = Path.Combine(_dir, "new folder", "mod_9999999_P.utoc");
        var outputs = new List<string>();

        await RetocProcess.StagedAsync(output, writesCompanionPak: true, Build("new", outputs: outputs));

        Assert.Equal("mod_9999999_P.utoc", Path.GetFileName(Assert.Single(outputs)));
        Assert.Equal(
            new Dictionary<string, string> { ["mod_9999999_P.utoc"] = "new.utoc", ["mod_9999999_P.ucas"] = "new.ucas", ["mod_9999999_P.pak"] = "new.pak" },
            Contents(Path.GetDirectoryName(output)!));
    }

    [Fact]
    public async Task APack_ReplacesAnExistingContainer()
    {
        var output = Path.Combine(_dir, "mod_P.utoc");
        await WriteContainerAsync(output, "old");

        await RetocProcess.StagedAsync(output, writesCompanionPak: true, Build("new"));

        Assert.Equal(
            new Dictionary<string, string> { ["mod_P.utoc"] = "new.utoc", ["mod_P.ucas"] = "new.ucas", ["mod_P.pak"] = "new.pak" },
            Contents(_dir));
    }

    [Theory]
    [InlineData(true, "new.pak")]
    [InlineData(false, "unrelated")]
    public async Task ThePakRetocWritesBesideTheContainer_ReplacesAnExistingOne_OnlyWhenItIsPartOfTheContainer(bool writesCompanionPak, string expected)
    {
        var output = Path.Combine(_dir, "mod_P.utoc");
        await File.WriteAllTextAsync(Path.ChangeExtension(output, ".pak"), "unrelated", TestContext.Current.CancellationToken);

        await RetocProcess.StagedAsync(output, writesCompanionPak, Build("new"));

        Assert.Equal(
            new Dictionary<string, string> { ["mod_P.utoc"] = "new.utoc", ["mod_P.ucas"] = "new.ucas", ["mod_P.pak"] = expected },
            Contents(_dir));
    }

    [Fact]
    public async Task ThePakRetocWritesBesideAnotherGamesContainer_FillsAFreeName()
    {
        var output = Path.Combine(_dir, "mod_P.utoc");

        await RetocProcess.StagedAsync(output, writesCompanionPak: false, Build("new"));

        Assert.Equal("new.pak", Contents(_dir)["mod_P.pak"]);
    }
}
