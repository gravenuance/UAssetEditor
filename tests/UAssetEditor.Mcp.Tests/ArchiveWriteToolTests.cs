using System.Text;
using ModelContextProtocol;
using UAssetEditor.Core.AssetSources.IoStore;
using UAssetEditor.Core.Tests;
using UAssetEditor.Mcp.Tests.Rig;

namespace UAssetEditor.Mcp.Tests;

// Pack, unpack, convert and repack run the bundled pak worker and retoc, which are Windows builds, as the Core pak tests do.
public sealed class ArchiveWriteToolTests : IDisposable
{
    private readonly McpRig _rig = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _rig.Dispose();

    private string MakePak(string name, IReadOnlyDictionary<string, byte[]> files)
    {
        var temp = TestPaks.CreatePak(files);
        var path = _rig.Folder.Combine(name);
        File.Move(temp, path);
        return path;
    }

    private string MakePak(string name, params string[] entries) =>
        MakePak(name, entries.ToDictionary(e => e, e => Encoding.UTF8.GetBytes(e)));

    private string SourceFolder(params string[] files)
    {
        var root = _rig.Folder.Combine("src", "mod");
        foreach (var file in files)
        {
            var path = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file);
        }
        return root;
    }

    // unpack_pak

    [Fact]
    public async Task UnpackPak_ExtractsOnlyTheMatchingEntries_UnderTheirInternalPaths()
    {
        var pak = MakePak("a.pak", "Content/A.txt", "Content/B.txt", "Other/C.txt");
        var destination = _rig.Folder.Combine("out");
        var progress = new CollectedProgress();

        var result = await _rig.ArchiveWrites.UnpackPakAsync(pak, destination, "Content/", progress: progress, cancellationToken: _ct);

        Assert.Equal(2, result.Matched);
        Assert.Equal(2, result.Unpacked);
        Assert.Empty(result.Failed);
        Assert.Equal("Content/A.txt", await File.ReadAllTextAsync(Path.Combine(destination, "Content", "A.txt"), _ct));
        Assert.False(File.Exists(Path.Combine(destination, "Other", "C.txt")));
        Assert.Contains(progress.Values, p => p.Progress == 2 && p.Total == 2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnpackPak_RequiresAFilter(string filter)
    {
        var pak = MakePak("a.pak", "Content/A.txt");
        var destination = _rig.Folder.Combine("out");

        var error = await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.UnpackPakAsync(pak, destination, filter, cancellationToken: _ct));

        Assert.Contains("filter is required", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task UnpackPak_RefusesWhenMoreEntriesMatchThanMaxFiles_AndWritesNothing()
    {
        var pak = MakePak("a.pak", "Content/A.txt", "Content/B.txt", "Content/C.txt");
        var destination = _rig.Folder.Combine("out");

        var error = await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.UnpackPakAsync(pak, destination, "Content", maxFiles: 2, cancellationToken: _ct));

        Assert.Contains("matches 3 entries", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task UnpackPak_RefusesToReplaceAFile_UnlessOverwriteIsSet()
    {
        var pak = MakePak("a.pak", "Content/A.txt");
        var destination = _rig.Folder.Combine("out");
        var existing = Path.Combine(destination, "Content", "A.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllTextAsync(existing, "mine", _ct);

        var error = await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.UnpackPakAsync(pak, destination, "A.txt", cancellationToken: _ct));
        Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
        Assert.Equal("mine", await File.ReadAllTextAsync(existing, _ct));

        await _rig.ArchiveWrites.UnpackPakAsync(pak, destination, "A.txt", overwrite: true, cancellationToken: _ct);
        Assert.Equal("Content/A.txt", await File.ReadAllTextAsync(existing, _ct));
    }

    [Fact]
    public async Task UnpackPak_WhenNothingMatches_SaysSo()
    {
        var pak = MakePak("a.pak", "Content/A.txt");

        var result = await _rig.ArchiveWrites.UnpackPakAsync(pak, _rig.Folder.Combine("out"), "Zzz", cancellationToken: _ct);

        Assert.Equal(0, result.Matched);
        Assert.Contains("No entry matched", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnpackPak_RefusesBadInput_AndHonoursCancellation()
    {
        var pak = MakePak("a.pak", "Content/A.txt");
        var destination = _rig.Folder.Combine("out");

        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.UnpackPakAsync(_rig.Folder.Combine("no.pak"), destination, "A", cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.UnpackPakAsync(pak, "relative/out", "A", cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.UnpackPakAsync(pak, destination, "A", maxFiles: 0, cancellationToken: _ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.ArchiveWrites.UnpackPakAsync(pak, destination, "A", cancellationToken: McpRig.Cancelled));
        Assert.False(File.Exists(Path.Combine(destination, "Content", "A.txt")));
    }

    // pack

    [Fact]
    public async Task Pack_ToPak_WritesAnArchiveHoldingEveryFile_WithTheDefaultMountPoint()
    {
        var source = SourceFolder("Content/A.txt", "Content/Sub/B.txt");
        var output = _rig.Folder.Combine("out", "mod.pak");
        var progress = new CollectedProgress();

        var result = await _rig.ArchiveWrites.PackAsync(source, output, progress: progress, cancellationToken: _ct);

        Assert.Equal(2, result.Files);
        Assert.Equal("pak", result.Format);
        var listed = await _rig.ArchiveReads.PakListAsync(output, cancellationToken: _ct);
        Assert.Equal(["Content/A.txt", "Content/Sub/B.txt"], listed.Entries.Select(e => e.Path));
        Assert.Equal("../../../mod/", (await _rig.ArchiveReads.PakInfoAsync(output, cancellationToken: _ct)).MountPoint);
        Assert.Contains(progress.Values, p => p.Progress == 2 && p.Total == 2);
    }

    [Fact]
    public async Task Pack_ToPak_TakesAMountPointAndPakVersion()
    {
        var source = SourceFolder("Content/A.txt");
        var output = _rig.Folder.Combine("out", "mod.pak");

        await _rig.ArchiveWrites.PackAsync(source, output, mount: "../../../Marvel/", pakVersion: "v9", compression: "zlib", cancellationToken: _ct);

        var info = await _rig.ArchiveReads.PakInfoAsync(output, cancellationToken: _ct);
        Assert.Equal("../../../Marvel/", info.MountPoint);
        Assert.Equal("V9", info.Version);
    }

    [Fact]
    public async Task Pack_RefusesToReplaceAnExistingOutput_UnlessOverwriteIsSet()
    {
        var source = SourceFolder("Content/A.txt");
        var output = _rig.Folder.Combine("out", "mod.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "precious", _ct);

        var error = await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.PackAsync(source, output, cancellationToken: _ct));
        Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
        Assert.Equal("precious", await File.ReadAllTextAsync(output, _ct));

        await _rig.ArchiveWrites.PackAsync(source, output, overwrite: true, cancellationToken: _ct);
        Assert.Equal(1, (await _rig.ArchiveReads.PakListAsync(output, cancellationToken: _ct)).Total);
    }

    [Fact]
    public async Task Pack_RefusesBadArguments_BeforeWritingAnything()
    {
        var source = SourceFolder("Content/A.txt");
        var output = _rig.Folder.Combine("out", "mod.pak");

        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.PackAsync(_rig.Folder.Combine("nowhere"), output, cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.PackAsync(source, _rig.Folder.Combine("out", "mod.zip"), cancellationToken: _ct));
        var inside = await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.PackAsync(source, Path.Combine(source, "mod.pak"), cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.PackAsync(source, output, pakVersion: "V99", cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.PackAsync(source, output, pakVersion: "11", cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.PackAsync(source, output, compression: "rot13", cancellationToken: _ct));
        var rivals = await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.PackAsync(source, output, kawaiiPhysics: true, cancellationToken: _ct));

        Assert.Contains("must not be inside", inside.Message, StringComparison.Ordinal);
        Assert.Contains("only apply to IoStore", rivals.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(Path.Combine(source, "mod.pak")));
    }

    [Fact]
    public async Task Pack_WhenCancelled_Throws_AndLeavesNoArchive()
    {
        var source = SourceFolder("Content/A.txt");
        var output = _rig.Folder.Combine("out", "mod.pak");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.ArchiveWrites.PackAsync(source, output, cancellationToken: McpRig.Cancelled));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Pack_ToIoStore_WritesTheContainerPair_AndRefusesToReplaceItUnlessOverwriteIsSet()
    {
        var source = SourceFolder("Content/Foo.txt");
        var output = _rig.Folder.Combine("out", "mod.utoc");

        var result = await _rig.ArchiveWrites.PackAsync(source, output, engineVersion: "VER_UE5_3", cancellationToken: _ct);

        Assert.StartsWith("iostore", result.Format, StringComparison.Ordinal);
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(Path.ChangeExtension(output, ".ucas")));
        var error = await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.PackAsync(source, output, engineVersion: "VER_UE5_3", cancellationToken: _ct));
        Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
        await _rig.ArchiveWrites.PackAsync(source, output, overwrite: true, engineVersion: "VER_UE5_3", cancellationToken: _ct);
    }

    [Fact]
    public async Task Pack_ToIoStore_TreatsASamePakBesideItAsPartOfTheContainerOnlyForRivals()
    {
        var source = SourceFolder("Content/Foo.txt");
        var output = _rig.Folder.Combine("out", "mod.utoc");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(Path.ChangeExtension(output, ".pak"), "an unrelated pak", _ct);

        var rivals = await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.PackAsync(source, output, game: "rivals", cancellationToken: _ct));
        await _rig.ArchiveWrites.PackAsync(source, output, engineVersion: "VER_UE5_3", cancellationToken: _ct);

        Assert.Contains("mod.pak already exists", rivals.Message, StringComparison.Ordinal);
        Assert.Equal("an unrelated pak", await File.ReadAllTextAsync(Path.ChangeExtension(output, ".pak"), _ct));
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task Pack_ToIoStore_RefusesTheRivalsOnlyOptionsForAnotherGame_AndPatchesWithoutAUsmap()
    {
        var source = SourceFolder("Content/Foo.txt");
        var output = _rig.Folder.Combine("out", "mod.utoc");

        var other = await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.PackAsync(source, output, engineVersion: "VER_UE5_3", obfuscate: true, cancellationToken: _ct));
        var noUsmap = await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.PackAsync(source, output, game: "rivals", kawaiiPhysics: true, cancellationToken: _ct));
        var oldEngine = await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.PackAsync(source, output, engineVersion: "VER_UE4_10", cancellationToken: _ct));

        Assert.Contains("only supported with game rivals", other.Message, StringComparison.Ordinal);
        Assert.Contains("need a usmap", noUsmap.Message, StringComparison.Ordinal);
        Assert.Contains("no IoStore equivalent", oldEngine.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    // to_legacy

    [Fact]
    public async Task ToLegacy_RefusesAnOutputThatWouldBeReplaced_AndBadChoices()
    {
        var source = SourceFolder("Content/Foo.txt");
        var utoc = _rig.Folder.Combine("out", "mod.utoc");
        await _rig.ArchiveWrites.PackAsync(source, utoc, engineVersion: "VER_UE5_3", cancellationToken: _ct);
        var folder = _rig.Folder.Combine("legacy");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "keep.txt"), "mine", _ct);
        var pak = _rig.Folder.Combine("legacy.pak");
        await File.WriteAllTextAsync(pak, "mine", _ct);

        var nonEmpty = await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.ToLegacyAsync(utoc, folder, cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.ToLegacyAsync(utoc, pak, cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.ToLegacyAsync(utoc, _rig.Folder.Combine("new"), layer: "both", cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.ToLegacyAsync(utoc, _rig.Folder.Combine("new"), paks: _rig.Folder.Combine("no-paks"), cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.ToLegacyAsync(_rig.Folder.Combine("no.utoc"), _rig.Folder.Combine("new"), cancellationToken: _ct));

        Assert.Contains("already exists", nonEmpty.Message, StringComparison.Ordinal);
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(folder, "keep.txt"), _ct));
        Assert.Equal("mine", await File.ReadAllTextAsync(pak, _ct));
    }

    [Fact]
    public async Task ToLegacy_OfAContainerWithNoPackages_ReportsRetocsReason()
    {
        var source = SourceFolder("Content/Foo.txt");
        var utoc = _rig.Folder.Combine("out", "mod.utoc");
        await _rig.ArchiveWrites.PackAsync(source, utoc, engineVersion: "VER_UE5_3", cancellationToken: _ct);

        var error = await Assert.ThrowsAsync<IoStoreConversionException>(() => _rig.ArchiveWrites.ToLegacyAsync(utoc, _rig.Folder.Combine("legacy"), noPaksResolve: true, cancellationToken: _ct));

        Assert.Equal("No assets converted - see the log for retoc's reason.", error.UserMessage);
    }

    [Fact]
    public async Task ToLegacy_WhenCancelled_Throws()
    {
        var source = SourceFolder("Content/Foo.txt");
        var utoc = _rig.Folder.Combine("out", "mod.utoc");
        await _rig.ArchiveWrites.PackAsync(source, utoc, engineVersion: "VER_UE5_3", cancellationToken: _ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _rig.ArchiveWrites.ToLegacyAsync(utoc, _rig.Folder.Combine("legacy"), cancellationToken: McpRig.Cancelled));
    }

    // repack

    [Fact]
    public async Task Repack_WritesANewPakWithTheSameEntries_AndLeavesTheSourceAlone()
    {
        var pak = MakePak("a.pak", "Content/A.txt", "Content/B.txt");
        var before = await File.ReadAllBytesAsync(pak, _ct);
        var output = _rig.Folder.Combine("out", "b.pak");

        var result = await _rig.ArchiveWrites.RepackAsync(pak, output, cancellationToken: _ct);

        Assert.Equal(2, result.Entries);
        Assert.Equal(0, result.AssetsEdited);
        Assert.Equal(["Content/A.txt", "Content/B.txt"], (await _rig.ArchiveReads.PakListAsync(output, cancellationToken: _ct)).Entries.Select(e => e.Path));
        Assert.Equal(before, await File.ReadAllBytesAsync(pak, _ct));
    }

    [Fact]
    public async Task Repack_WithARuleset_EditsTheAssetsInsideTheNewPak()
    {
        var sample = AssetFixtures.WriteSample(_rig.Folder, "build/Sample.uasset");
        var pak = MakePak("a.pak", new Dictionary<string, byte[]>
        {
            ["Content/Sample.uasset"] = await File.ReadAllBytesAsync(sample, _ct),
            ["Content/Sample.uexp"] = await File.ReadAllBytesAsync(Path.ChangeExtension(sample, ".uexp"), _ct),
        });
        var output = _rig.Folder.Combine("out", "b.pak");
        const string ruleset = """{"scope":{"propertyNameTerms":[{"text":"Count"}]},"rules":[{"kind":"setValue","newValue":"9"}]}""";

        var result = await _rig.ArchiveWrites.RepackAsync(pak, output, ruleset, cancellationToken: _ct);
        await _rig.ArchiveWrites.UnpackPakAsync(output, _rig.Folder.Combine("un"), "Sample", cancellationToken: _ct);

        Assert.Equal(1, result.AssetsEdited);
        Assert.Equal(1, result.EditsApplied);
        Assert.Equal("9", AssetFixtures.ReadProperty(_rig.Folder.Combine("un", "Content", "Sample.uasset"), "Count"));
        Assert.Equal("5", AssetFixtures.ReadProperty(sample, "Count"));
    }

    [Fact]
    public async Task Repack_RefusesAnExistingOutputTheSourceItselfAndABadRuleset()
    {
        var pak = MakePak("a.pak", "Content/A.txt");
        var existing = MakePak("b.pak", "Content/B.txt");

        var replaced = await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.RepackAsync(pak, existing, cancellationToken: _ct));
        var same = await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.RepackAsync(pak, pak, overwrite: true, cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.ArchiveWrites.RepackAsync(pak, _rig.Folder.Combine("out.pak"), "{ not json", cancellationToken: _ct));

        Assert.Contains("already exists", replaced.Message, StringComparison.Ordinal);
        Assert.Contains("same path", same.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_rig.Folder.Combine("out.pak")));
    }

    [Fact]
    public async Task Repack_WhenCancelled_Throws_AndLeavesNoOutput()
    {
        var pak = MakePak("a.pak", "Content/A.txt");
        var output = _rig.Folder.Combine("out", "b.pak");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.ArchiveWrites.RepackAsync(pak, output, cancellationToken: McpRig.Cancelled));

        Assert.False(File.Exists(output));
    }
}
