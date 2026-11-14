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
    private readonly GameProfile _rivals = GameProfile.For(Game.MarvelRivals)!;

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

        var path = AssetIo.UseStoredUsmap(_rivals, _store, diagnostics);

        Assert.Null(path);
        var text = diagnostics.ToString();
        Assert.StartsWith("Warning: --game rivals without --usmap", text, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_dir, "store", "rivals"), text, StringComparison.Ordinal);
        Assert.Contains("UASSETEDITOR_MAPPINGS_DIR", text, StringComparison.Ordinal);
        Assert.Contains("uacli usmap add <file> --game rivals", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoredUsmap_IsUsed_AndAnnouncedWithoutAWarning()
    {
        var source = Path.Combine(_dir, "ff7r.usmap");
        File.WriteAllBytes(source, EmptyUsmap);
        _store.Add(source, _ff7r, build: 196);
        using var diagnostics = new StringWriter();

        var path = AssetIo.UseStoredUsmap(_ff7r, _store, diagnostics);

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
}
