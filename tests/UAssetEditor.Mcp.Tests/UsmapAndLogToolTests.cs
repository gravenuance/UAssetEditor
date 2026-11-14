using ModelContextProtocol;
using UAssetEditor.Core.Tests.Games;
using UAssetEditor.Mcp.Tests.Rig;
using UAssetEditor.Mcp.Tools;
using UAssetEditor.Tooling;

namespace UAssetEditor.Mcp.Tests;

public sealed class UsmapAndLogToolTests : IDisposable
{
    // magic 0x30C4, version Initial, no compression, 12-byte body of zero names, enums and schemas.
    private static readonly byte[] EmptyUsmap =
        [0xC4, 0x30, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly McpRig _rig = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _rig.Dispose();

    private string Usmap(string name = "dump.usmap")
    {
        var path = _rig.Folder.Combine("dumps", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EmptyUsmap);
        return path;
    }

    // usmap_list / usmap_add

    [Fact]
    public async Task UsmapList_OfAnEmptyStore_IsEmpty_AndNamesTheStore()
    {
        var result = await _rig.Usmaps.UsmapListAsync(cancellationToken: _ct);

        Assert.Empty(result.Usmaps);
        Assert.Equal(_rig.Folder.Combine("maps"), result.Store);
    }

    [Fact]
    public async Task UsmapAdd_StoresTheFile_AndTheListShowsItNewestFirst()
    {
        await _rig.Usmaps.UsmapAddAsync(Usmap("old.usmap"), "ff7r", build: 196, cancellationToken: _ct);

        var added = await _rig.Usmaps.UsmapAddAsync(Usmap("5.3.2-205+++x-FF7R.usmap"), "ff7r", cancellationToken: _ct);
        var list = await _rig.Usmaps.UsmapListAsync("ff7r", _ct);

        Assert.Equal(205, added.Stored.Build);
        Assert.Equal([205, 196], list.Usmaps.Select(u => u.Build));
        Assert.All(list.Usmaps, u => Assert.True(File.Exists(u.Path)));
        Assert.Equal("", added.Note);
    }

    [Fact]
    public async Task UsmapAdd_RefusesABuildThatIsAlreadyStored_UnlessForced()
    {
        var first = await _rig.Usmaps.UsmapAddAsync(Usmap(), "ff7r", build: 196, cancellationToken: _ct);

        var error = await Assert.ThrowsAsync<McpException>(() => _rig.Usmaps.UsmapAddAsync(Usmap("again.usmap"), "ff7r", build: 196, cancellationToken: _ct));
        var forced = await _rig.Usmaps.UsmapAddAsync(Usmap("again.usmap"), "ff7r", build: 196, force: true, cancellationToken: _ct);

        Assert.Contains("already stored", error.Message, StringComparison.Ordinal);
        Assert.Contains("force=true", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", error.Message, StringComparison.Ordinal);
        Assert.Equal(196, forced.Stored.Build);
        Assert.Single((await _rig.Usmaps.UsmapListAsync("ff7r", _ct)).Usmaps);
        Assert.NotNull(first);
    }

    [Fact]
    public async Task UsmapAdd_RefusesAFileThatIsNotAUsmap_AMissingOneAndAnUnknownGame()
    {
        var text = _rig.Folder.Combine("fake.usmap");
        await File.WriteAllTextAsync(text, "not a usmap", _ct);
        var notUsmap = _rig.Folder.Combine("fake.txt");
        await File.WriteAllTextAsync(notUsmap, "x", _ct);

        var unreadable = await Assert.ThrowsAsync<McpException>(() => _rig.Usmaps.UsmapAddAsync(text, "ff7r", build: 1, cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.Usmaps.UsmapAddAsync(notUsmap, "ff7r", cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.Usmaps.UsmapAddAsync(_rig.Folder.Combine("none.usmap"), "ff7r", cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.Usmaps.UsmapAddAsync(Usmap(), "halflife", cancellationToken: _ct));

        Assert.Contains("not a readable usmap", unreadable.Message, StringComparison.Ordinal);
        Assert.Empty((await _rig.Usmaps.UsmapListAsync(cancellationToken: _ct)).Usmaps);
    }

    [Fact]
    public async Task UsmapAdd_ForRivalsWithoutABuild_WarnsItCannotTellWhenItIsStale()
    {
        var result = await _rig.Usmaps.UsmapAddAsync(Usmap("Marvel.usmap"), "rivals", cancellationToken: _ct);

        Assert.Null(result.Stored.Build);
        Assert.Contains("out of date", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsmapList_RefusesAnUnknownGame_AndHonoursCancellation()
    {
        await Assert.ThrowsAsync<McpException>(() => _rig.Usmaps.UsmapListAsync("halflife", _ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.Usmaps.UsmapListAsync(cancellationToken: McpRig.Cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.Usmaps.UsmapAddAsync(Usmap(), "ff7r", cancellationToken: McpRig.Cancelled));
        Assert.Empty((await _rig.Usmaps.UsmapListAsync(cancellationToken: _ct)).Usmaps);
    }

    [Fact]
    public async Task AToolGivenAGame_PicksUpAUsmapAddedAfterItsFirstCall()
    {
        var asset = AssetFixtures.WriteSample(_rig.Folder);
        await _rig.Usmaps.UsmapAddAsync(Usmap("a.usmap"), "ff7r", build: 100, cancellationToken: _ct);
        var first = await _rig.Assets.ListExportsAsync(asset, game: "ff7r", engineVersion: "VER_UE4_27", cancellationToken: _ct);

        await _rig.Usmaps.UsmapAddAsync(Usmap("b.usmap"), "ff7r", build: 200, cancellationToken: _ct);
        var second = await _rig.Assets.ListExportsAsync(asset, game: "ff7r", engineVersion: "VER_UE4_27", cancellationToken: _ct);

        Assert.Contains(first.Warnings, w => w.Contains("build 100", StringComparison.Ordinal));
        Assert.Contains(second.Warnings, w => w.Contains("build 200", StringComparison.Ordinal));
    }

    [Fact]
    public void OpenSettings_PassTheGameVersionUsmapAndStrictOnToTheOpenCode()
    {
        var usmap = Usmap();

        var settings = OpenSettings.Create(_rig.Store, "FF7R", "ver_ue5_3", usmap, strict: true);

        Assert.Equal("ff7r", settings.Profile!.CliName);
        Assert.Equal(UAssetAPI.UnrealTypes.EngineVersion.VER_UE5_3, settings.EngineVersion);
        Assert.Equal(usmap, settings.UsmapPath);
        Assert.True(settings.Args.Flag("strict"));
        Assert.Equal(usmap, settings.Args.Option("usmap"));
    }

    [Fact]
    public void OpenSettings_WithNothing_UsesTheDefaults_AndTheGamesEngineVersionWhenAGameIsGiven()
    {
        var none = OpenSettings.Create(_rig.Store, null, null, null);
        var rivals = OpenSettings.Create(_rig.Store, "rivals", null, null);

        Assert.Equal(UAssetAPI.UnrealTypes.EngineVersion.VER_UE4_27, none.EngineVersion);
        Assert.Null(none.UsmapPath);
        Assert.Equal(UAssetAPI.UnrealTypes.EngineVersion.VER_UE5_3, rivals.EngineVersion);
    }

    [Theory]
    [InlineData("zz")]
    [InlineData("0xZZ")]
    public void OpenSettings_RefusesAKeyThatIsNotHex_WithoutRepeatingIt(string key)
    {
        var error = Assert.Throws<McpException>(() => OpenSettings.Create(_rig.Store, null, null, null, aesKey: key));

        Assert.DoesNotContain(key, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenSettings_GivenAKey_KeepsItForThePakCallsOnly()
    {
        var settings = OpenSettings.Create(_rig.Store, null, null, null, aesKey: "0x00112233");

        Assert.Equal([0x00, 0x11, 0x22, 0x33], settings.AesKey);
        Assert.DoesNotContain("00112233", string.Join(' ', settings.Warnings()), StringComparison.OrdinalIgnoreCase);
    }

    // game_log_summary

    private async Task<string> WriteLogAsync(string name, params byte[][] frames)
    {
        var path = _rig.Folder.Combine(name);
        await File.WriteAllBytesAsync(path, RivalsLogTestData.Log(frames), _ct);
        return path;
    }

    [Fact]
    public async Task GameLogSummary_ReadsTheEngineBuild_AndFindsNoCrash()
    {
        var log = await WriteLogAsync("a.log", RivalsLogTestData.Frame(RivalsLogTestData.EngineLine4008108Cipher));

        var result = await GameLogTools.GameLogSummaryAsync(log, cancellationToken: _ct);

        Assert.Equal(log, result.Log);
        Assert.Equal("5.3.2-4008108+++depot_marvel+S10.5_release", result.Engine);
        Assert.Equal(4008108, result.Build);
        Assert.False(result.Crashed);
        Assert.Empty(result.Mods);
        Assert.Null(result.Diff);
    }

    [Fact]
    public async Task GameLogSummary_WithASecondLog_ComparesTheMountedMods()
    {
        var first = await WriteLogAsync("a.log", RivalsLogTestData.Frame(RivalsLogTestData.EngineLine4008108Cipher));
        var second = await WriteLogAsync("b.log", RivalsLogTestData.Frame(RivalsLogTestData.EngineLine3939753Cipher));

        var result = await GameLogTools.GameLogSummaryAsync(first, second, _ct);

        Assert.NotNull(result.Diff);
        Assert.Empty(result.Diff.OnlyInLog);
        Assert.Empty(result.Diff.OnlyInOther);
    }

    [Fact]
    public async Task GameLogSummary_NotesARecordThatFailedToInflate()
    {
        var log = await WriteLogAsync("a.log", RivalsLogTestData.Frame(RivalsLogTestData.EngineLine4008108Cipher), RivalsLogTestData.Frame(RivalsLogTestData.CorruptZlibCipher));

        var result = await GameLogTools.GameLogSummaryAsync(log, cancellationToken: _ct);

        Assert.Contains(result.Notes, n => n.Contains("failed to inflate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GameLogSummary_RefusesAFileThatIsNotALog_AMissingOneAndAWrongExtension()
    {
        var junk = _rig.Folder.Combine("junk.log");
        await File.WriteAllBytesAsync(junk, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15], _ct);
        var text = _rig.Folder.Combine("a.txt");
        await File.WriteAllTextAsync(text, "x", _ct);

        await Assert.ThrowsAsync<ArgException>(() => GameLogTools.GameLogSummaryAsync(junk, cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => GameLogTools.GameLogSummaryAsync(_rig.Folder.Combine("none.log"), cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => GameLogTools.GameLogSummaryAsync(text, cancellationToken: _ct));
    }

    [Fact]
    public async Task GameLogSummary_WhenCancelled_Throws()
    {
        var log = await WriteLogAsync("a.log", RivalsLogTestData.Frame(RivalsLogTestData.EngineLine4008108Cipher));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GameLogTools.GameLogSummaryAsync(log, cancellationToken: McpRig.Cancelled));
    }
}
