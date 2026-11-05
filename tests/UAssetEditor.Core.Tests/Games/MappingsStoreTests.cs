using UAssetEditor.Core.Games;

namespace UAssetEditor.Core.Tests.Games;

public sealed class MappingsStoreTests : IDisposable
{
    private const string DumperName = "5.3.2-3939753+++depot_marvel+S10.5_release-Marvel.usmap";

    // magic 0x30C4, version Initial, no compression, 12-byte body of zero names, enums and schemas.
    private static readonly byte[] EmptyUsmap =
        [0xC4, 0x30, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    // The same with one name ("A") in the name table, so the bytes differ.
    private static readonly byte[] OtherUsmap =
        [0xC4, 0x30, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 1, 0, 0, 0, 1, 0x41, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly string _dir =Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Mappings_" + Guid.NewGuid());
    private readonly MappingsStore _store;
    private readonly GameProfile _rivals = GameProfile.For(Game.MarvelRivals)!;
    private readonly GameProfile _ff7r = GameProfile.For(Game.FinalFantasy7Remake)!;
    private readonly GameProfile _stellarBlade = GameProfile.For(Game.StellarBlade)!;

    public MappingsStoreTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "source"));
        _store = new MappingsStore(Path.Combine(_dir, "store"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Source(string name, byte[]? content = null)
    {
        var path = Path.Combine(_dir, "source", name);
        File.WriteAllBytes(path, content ?? EmptyUsmap);
        return path;
    }

    [Fact]
    public void Add_ParsesTheBuildFromADumperName_AndKeepsTheName()
    {
        var stored = _store.Add(Source(DumperName), _rivals);

        Assert.Equal(3939753, stored.Build);
        Assert.Equal(Path.Combine(_dir, "store", "rivals", DumperName), stored.Path);
        Assert.Equal(EmptyUsmap, File.ReadAllBytes(stored.Path));
    }

    [Fact]
    public void Add_WithAnExplicitBuild_PrefixesANameThatLacksIt()
    {
        var stored = _store.Add(Source("Marvel.usmap"), _rivals, build: 4008108);

        Assert.Equal(4008108, stored.Build);
        Assert.Equal("4008108-Marvel.usmap", Path.GetFileName(stored.Path));
        Assert.Equal(4008108, _store.Latest(_rivals)?.Build);
    }

    [Fact]
    public void Add_WithoutAnyBuild_StoresItUnderItsOwnName_AsTheGamesOnlyUsmap()
    {
        var stored = _store.Add(Source("StellarBlade.usmap"), _stellarBlade);

        Assert.Null(stored.Build);
        Assert.Equal(Path.Combine(_dir, "store", "stellarblade", "StellarBlade.usmap"), stored.Path);
        Assert.Equal(stored, _store.Latest(_stellarBlade));
    }

    [Fact]
    public void Add_ASecondUsmapWithoutABuild_NeedsForce_AndForceReplacesTheFirst()
    {
        var first = _store.Add(Source("StellarBlade.usmap"), _stellarBlade);

        var ex = Assert.Throws<MappingsStoreException>(() => _store.Add(Source("SB_new.usmap", OtherUsmap), _stellarBlade));
        Assert.Contains("without a build", ex.Message, StringComparison.Ordinal);

        var second = _store.Add(Source("SB_new.usmap", OtherUsmap), _stellarBlade, force: true);

        Assert.False(File.Exists(first.Path));
        Assert.Equal([second], _store.List(_stellarBlade));
        Assert.Equal(OtherUsmap, File.ReadAllBytes(second.Path));
    }

    [Fact]
    public void Latest_PrefersAnyBuild_OverTheUsmapWithoutOne()
    {
        _store.Add(Source("hand.usmap"), _rivals);
        _store.Add(Source(DumperName), _rivals);

        Assert.Equal(3939753, _store.Latest(_rivals)?.Build);
        Assert.Equal([3939753, null], _store.List(_rivals).Select(e => e.Build));
    }

    [Fact]
    public void Add_ABuildThatContradictsTheName_IsRefused()
    {
        var ex = Assert.Throws<MappingsStoreException>(() => _store.Add(Source(DumperName), _rivals, build: 111));

        Assert.Contains("3939753", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_RejectsAFileThatIsNotAUsmapByName()
    {
        Assert.Throws<MappingsStoreException>(() => _store.Add(Source("5.3.2-1+++x.txt"), _rivals));
    }

    [Fact]
    public void Add_RejectsAFileThatDoesNotLoadAsAUsmap_AndLeavesNothingBehind()
    {
        var bad = Source("5.3.2-77+++depot.usmap", [1, 2, 3, 4, 5, 6, 7, 8]);

        var ex = Assert.Throws<MappingsStoreException>(() => _store.Add(bad, _rivals));

        Assert.Contains("usmap", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_store.List(_rivals));
        Assert.Empty(Directory.GetFiles(_store.DirectoryFor(_rivals)));
    }

    [Fact]
    public void Add_AMissingFile_IsAnError()
    {
        Assert.Throws<MappingsStoreException>(() => _store.Add(Path.Combine(_dir, "source", DumperName), _rivals));
    }

    [Fact]
    public void Add_TheSameBuildTwice_NeedsForce()
    {
        _store.Add(Source(DumperName), _rivals);

        Assert.Throws<MappingsStoreException>(() => _store.Add(Source(DumperName, OtherUsmap), _rivals));
        Assert.Equal(EmptyUsmap, File.ReadAllBytes(_store.Latest(_rivals)!.Path));

        _store.Add(Source(DumperName, OtherUsmap), _rivals, force: true);
        Assert.Equal(OtherUsmap, File.ReadAllBytes(_store.Latest(_rivals)!.Path));
    }

    [Fact]
    public void Add_ForceReplacesTheSameBuildStoredUnderAnotherName()
    {
        _store.Add(Source("old.usmap"), _rivals, build: 3939753);

        _store.Add(Source(DumperName), _rivals, force: true);

        var entry = Assert.Single(_store.List(_rivals));
        Assert.Equal(DumperName, Path.GetFileName(entry.Path));
    }

    [Fact]
    public void Latest_IsTheHighestBuild_NotTheNewestFileOrAlphabeticallyLast()
    {
        _store.Add(Source("a.usmap"), _rivals, build: 3939753);
        _store.Add(Source("b.usmap"), _rivals, build: 999999);
        // The lower build is written last and sorts last by name.
        File.SetLastWriteTimeUtc(_store.List(_rivals).Single(e => e.Build == 999999).Path, DateTime.UtcNow.AddDays(1));

        Assert.Equal(3939753, _store.Latest(_rivals)?.Build);
        Assert.Equal([3939753, 999999], _store.List(_rivals).Select(e => e.Build));
    }

    [Fact]
    public void Games_HaveSeparateFolders_AndAnEmptyOneHasNoLatest()
    {
        _store.Add(Source(DumperName), _rivals);

        Assert.Null(_store.Latest(_ff7r));
        Assert.Empty(_store.List(_ff7r));
        Assert.EndsWith("ff7r", _store.DirectoryFor(_ff7r), StringComparison.Ordinal);
    }

    [Fact]
    public void List_ShowsHandPlacedFilesWithoutABuild_AfterTheBuilds()
    {
        _store.Add(Source(DumperName), _rivals);
        File.WriteAllBytes(Path.Combine(_store.DirectoryFor(_rivals), "hand-placed.usmap"), EmptyUsmap);

        Assert.Equal(
            [new StoredMappings(3939753, Path.Combine(_store.DirectoryFor(_rivals), DumperName)), new StoredMappings(null, Path.Combine(_store.DirectoryFor(_rivals), "hand-placed.usmap"))],
            _store.List(_rivals));
    }

    [Theory]
    [InlineData("5.3.2-3939753+++depot_marvel+S10.5_release-Marvel.usmap", 3939753)]
    [InlineData("4008108-Marvel.usmap", 4008108)]
    [InlineData("Marvel.usmap", null)]
    [InlineData("5.3.2-+++depot.usmap", null)]
    [InlineData("99999999999-x.usmap", null)]
    public void ParseBuild_ReadsDumperNamesAndTheBuildPrefix(string name, int? expected)
    {
        Assert.Equal(expected, MappingsStore.ParseBuild(name));
    }

    [Fact]
    public void Default_UsesTheEnvironmentVariable_WhenSet()
    {
        var previous = Environment.GetEnvironmentVariable(MappingsStore.RootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(MappingsStore.RootEnvironmentVariable, _dir);
            Assert.Equal(Path.GetFullPath(_dir), MappingsStore.Default().Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MappingsStore.RootEnvironmentVariable, previous);
        }
    }
}
