using UAssetEditor.Cli;
using UAssetEditor.Core.Games;

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
}
