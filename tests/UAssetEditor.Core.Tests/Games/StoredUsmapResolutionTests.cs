using UAssetEditor.Core.Games;
using UAssetEditor.Tooling;

namespace UAssetEditor.Core.Tests.Games;

public sealed class StoredUsmapResolutionTests : IDisposable
{
    // magic 0x30C4, version Initial, no compression, 12-byte body of zero names, enums and schemas.
    private static readonly byte[] EmptyUsmap =
        [0xC4, 0x30, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_StoredUsmap_" + Guid.NewGuid());
    private readonly MappingsStore _store;
    private readonly GameProfile _ff7r = GameProfile.For(Game.FinalFantasy7Remake)!;

    public StoredUsmapResolutionTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new MappingsStore(Path.Combine(_dir, "store"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void AnEmptyStore_ResolvesToNothing_AndWarnsWhereItLookedAndWhatToDo()
    {
        using var diagnostics = new StringWriter();

        var path = AssetIo.ResolveUsmapPath(new ArgReader(["--game", "rivals"]) { Diagnostics = diagnostics, Mappings = _store });

        Assert.Null(path);
        var text = diagnostics.ToString();
        Assert.StartsWith("Warning: no usmap is given or stored for rivals", text, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_dir, "store", "rivals"), text, StringComparison.Ordinal);
        Assert.Contains("UASSETEDITOR_MAPPINGS_DIR", text, StringComparison.Ordinal);
        Assert.EndsWith($"Pass --usmap or run 'uacli usmap add <file> --game rivals'.{Environment.NewLine}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoredUsmap_IsUsed_AndAnnouncedWithoutAWarning()
    {
        var source = Path.Combine(_dir, "ff7r.usmap");
        File.WriteAllBytes(source, EmptyUsmap);
        _store.Add(source, _ff7r, build: 196);
        using var diagnostics = new StringWriter();

        var path = AssetIo.UseStoredUsmap(_ff7r, _store, diagnostics, gameLogs: null, advice: "unused");

        Assert.Equal(Path.Combine(_dir, "store", "ff7r", "196-ff7r.usmap"), path);
        Assert.Equal($"Using stored usmap {path} (build 196).{Environment.NewLine}", diagnostics.ToString());
    }

    [Fact]
    public void ResolveUsmapPath_LooksTheStoreUpOncePerRun_ButSeesAUsmapStoredBeforeTheNextRun()
    {
        using var diagnostics = new StringWriter();
        var run = new ArgReader(["--game", "ff7r"]) { Mappings = _store, Diagnostics = diagnostics };
        Assert.Null(AssetIo.ResolveUsmapPath(run));

        var source = Path.Combine(_dir, "ff7r.usmap");
        File.WriteAllBytes(source, EmptyUsmap);
        _store.Add(source, _ff7r, build: 196);

        Assert.Null(AssetIo.ResolveUsmapPath(run)); // the same run keeps its first answer, and warned once
        Assert.Equal(1, diagnostics.ToString().Split("Warning:").Length - 1);
        var nextRun = new ArgReader(["--game", "ff7r"]) { Mappings = _store, Diagnostics = diagnostics };
        Assert.Equal(Path.Combine(_dir, "store", "ff7r", "196-ff7r.usmap"), AssetIo.ResolveUsmapPath(nextRun));
    }

    [Fact]
    public void WithOpenSettingsFrom_ShareTheRunsStoreLookupDiagnosticsAndStore()
    {
        using var diagnostics = new StringWriter();
        var run = new ArgReader(["--game", "ff7r"]) { Mappings = _store, Diagnostics = diagnostics };

        var op = new ArgReader(["--export", "0"]).WithOpenSettingsFrom(run);

        Assert.Same(run.Mappings, op.Mappings);
        Assert.Same(run.Diagnostics, op.Diagnostics);
        Assert.Same(run.StoredUsmaps, op.StoredUsmaps);
        Assert.Equal("ff7r", op.Option("game"));
    }

    private static string WriteGameLog(string folder, string engineLineCipher, DateTime writtenUtc)
    {
        Directory.CreateDirectory(folder);
        var log = Path.Combine(folder, "Marvel.log");
        File.WriteAllBytes(log, RivalsLogTestData.Log(RivalsLogTestData.Frame(engineLineCipher)));
        File.SetLastWriteTimeUtc(log, writtenUtc);
        return log;
    }

    private static int Warnings(StringWriter diagnostics) => diagnostics.ToString().Split("last ran build").Length - 1;

    [Fact]
    public void TheStaleUsmapCheck_RunsAgainWhenTheGameLogChanges_NotOncePerProcess()
    {
        var logs = Path.Combine(_dir, "logs");
        var usmap = Path.Combine(_dir, "3939753-rivals.usmap");
        File.WriteAllBytes(usmap, EmptyUsmap);
        var t0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        using var diagnostics = new StringWriter();
        ArgReader NewRun() => new(["--game", "rivals", "--usmap", usmap]) { Mappings = _store, Diagnostics = diagnostics, GameLogs = logs };

        // The game last ran build 4008108: the usmap is for an older build.
        WriteGameLog(logs, RivalsLogTestData.EngineLine4008108Cipher, t0);
        _ = AssetIo.ResolveUsmapPath(NewRun());
        Assert.Equal(1, Warnings(diagnostics));
        Assert.Contains("usmap is build 3939753 but Marvel Rivals last ran build 4008108", diagnostics.ToString(), StringComparison.Ordinal);

        // Nothing changed: the same answer is not repeated for every call.
        _ = AssetIo.ResolveUsmapPath(NewRun());
        Assert.Equal(1, Warnings(diagnostics));

        // The game ran again on the usmap's own build: the check passes quietly.
        WriteGameLog(logs, RivalsLogTestData.EngineLine3939753Cipher, t0.AddHours(1));
        _ = AssetIo.ResolveUsmapPath(NewRun());
        Assert.Equal(1, Warnings(diagnostics));

        // A patch, then another run of the game: the warning comes back without restarting the process.
        WriteGameLog(logs, RivalsLogTestData.EngineLine4008108Cipher, t0.AddHours(2));
        _ = AssetIo.ResolveUsmapPath(NewRun());
        Assert.Equal(2, Warnings(diagnostics));
    }

    [Fact]
    public void TheStaleUsmapCheck_WithoutAGameLog_StaysQuiet_AndChecksOnceALogAppears()
    {
        var logs = Path.Combine(_dir, "later-logs");
        var usmap = Path.Combine(_dir, "3939753-later.usmap");
        File.WriteAllBytes(usmap, EmptyUsmap);
        using var diagnostics = new StringWriter();
        ArgReader NewRun() => new(["--game", "rivals", "--usmap", usmap]) { Mappings = _store, Diagnostics = diagnostics, GameLogs = logs };

        _ = AssetIo.ResolveUsmapPath(NewRun());
        Assert.Equal(0, Warnings(diagnostics));

        WriteGameLog(logs, RivalsLogTestData.EngineLine4008108Cipher, new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc));
        _ = AssetIo.ResolveUsmapPath(NewRun());
        Assert.Equal(1, Warnings(diagnostics));
    }

    [Fact]
    public void LoadMappings_SharesOneInstanceForAnUnchangedFile()
    {
        var file = Path.Combine(_dir, "shared.usmap");
        File.WriteAllBytes(file, EmptyUsmap);

        Assert.Same(AssetIo.LoadMappings(file, "--usmap"), AssetIo.LoadMappings(file, "--usmap"));
    }

    [Fact]
    public void LoadMappings_LoadsAgainAFileReplacedInPlace()
    {
        // A long-running server sees "usmap add --force" overwrite the stored file at the same path.
        var file = Path.Combine(_dir, "replaced.usmap");
        File.WriteAllBytes(file, EmptyUsmap);
        var before = AssetIo.LoadMappings(file, "--usmap");

        File.WriteAllBytes(file, EmptyUsmap);
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(1));

        Assert.NotSame(before, AssetIo.LoadMappings(file, "--usmap"));
    }

    [Fact]
    public void LoadMappings_ForgetsTheOldLoadOfAFileReplacedInPlace_SoItsMemoryIsFreed()
    {
        var file = Path.Combine(_dir, "freed.usmap");
        File.WriteAllBytes(file, EmptyUsmap);
        _ = AssetIo.LoadMappings(file, "--usmap");
        Assert.Equal(1, AssetIo.CachedMappingsFor(file));

        for (var minute = 1; minute <= 3; minute++)
        {
            File.WriteAllBytes(file, EmptyUsmap);
            File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(minute));
            _ = AssetIo.LoadMappings(file, "--usmap");
        }

        Assert.Equal(1, AssetIo.CachedMappingsFor(file));
    }

    [Fact]
    public void LoadMappings_KeepsOneEntryPerFile_AndSeparateFilesStaySeparate()
    {
        var first = Path.Combine(_dir, "one.usmap");
        var second = Path.Combine(_dir, "two.usmap");
        File.WriteAllBytes(first, EmptyUsmap);
        File.WriteAllBytes(second, EmptyUsmap);

        var a = AssetIo.LoadMappings(first, "--usmap");
        var b = AssetIo.LoadMappings(second, "--usmap");

        Assert.NotSame(a, b);
        Assert.Equal(1, AssetIo.CachedMappingsFor(first));
        Assert.Equal(1, AssetIo.CachedMappingsFor(second));
    }

    [Fact]
    public void LoadMappings_AUsmapThatCannotBeRead_IsReportedAgainOnceTheFileChanges()
    {
        var file = Path.Combine(_dir, "broken.usmap");
        File.WriteAllBytes(file, [1, 2, 3]);
        Assert.ThrowsAny<Exception>(() => AssetIo.LoadMappings(file, "--usmap"));

        File.WriteAllBytes(file, EmptyUsmap);
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(1));

        Assert.NotNull(AssetIo.LoadMappings(file, "--usmap"));
    }
}
