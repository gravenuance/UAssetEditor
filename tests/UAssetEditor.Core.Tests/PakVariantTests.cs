using UAssetAPI;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.AssetSources.PakWorker;
using UAssetEditor.Core.Games;
using UAssetEditor.Tooling;

namespace UAssetEditor.Core.Tests;

/// <summary>A game's pak format (Marvel Rivals' encryption and padding) carried from the game profile to the native reader.</summary>
[Collection("Pak")]
public sealed class PakVariantTests : IDisposable
{
    private static readonly byte[] Key = PakAesKey.Parse(GameProfile.For(Game.MarvelRivals)!.AesKeyHex)!;
    private static readonly byte[] Contents = Enumerable.Range(0, 5000).Select(i => (byte)(i % 7)).ToArray();

    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files) TempPak.DeleteQuietly(file);
    }

    /// <summary>An encrypted, compressed pak written in the Marvel Rivals format, as the game's own are.</summary>
    private string MarvelRivalsPak()
    {
        var path = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_" + Guid.NewGuid() + ".pak");
        _files.Add(path);
        using var stream = File.Create(path);
        using var builder = new PakBuilder();
        builder.Key(Key).Variant(PakVariant.MarvelRivals).Compression([PakCompression.Zlib]);
        using var writer = builder.Writer(stream, PakVersion.V11, "../../../Marvel/");
        writer.WriteFile("Marvel/Content/Foo.uasset", Contents);
        writer.WriteIndex();
        return path;
    }

    [Fact]
    public void ProfileOfMarvelRivals_NamesItsFormat_AndNoOtherGameDoes()
    {
        Assert.Equal(PakVariant.MarvelRivals, GameProfile.For(Game.MarvelRivals)!.PakVariant);
        Assert.Equal(PakVariant.Standard, GameProfile.For(Game.FinalFantasy7Remake)!.PakVariant);
        Assert.Equal(PakVariant.Standard, GameProfile.For(Game.StellarBlade)!.PakVariant);
        Assert.Equal(PakVariant.Standard, GameProfile.ResolvePakVariant(null));
    }

    [Theory]
    [InlineData("rivals", PakVariant.MarvelRivals)]
    [InlineData("ff7r", PakVariant.Standard)]
    public void TheGameOption_DecidesTheFormatTheCliReads(string game, PakVariant expected)
    {
        Assert.Equal(expected, AssetIo.ResolvePakVariant(new ArgReader(["--game", game])));
        Assert.Equal(PakVariant.Standard, AssetIo.ResolvePakVariant(new ArgReader([])));
    }

    [Fact]
    public async Task AMarvelRivalsPak_ReadsThroughTheWorker_AsMarvelRivals()
    {
        var pak = MarvelRivalsPak();
        using var worker = new PakWorkerProcess();

        using var reader = new PakReaderHandle(worker, pak, Key, PakVariant.MarvelRivals);
        await reader.OpenAsync(TestContext.Current.CancellationToken);
        using var bytes = await reader.ReadEntryAsync("Marvel/Content/Foo.uasset", TestContext.Current.CancellationToken);

        Assert.Equal(["Marvel/Content/Foo.uasset"], reader.Entries);
        Assert.Equal(Contents, bytes.Memory.ToArray());
    }

    [Fact]
    public async Task AMarvelRivalsPak_IsNotReadAsAStandardOne()
    {
        var pak = MarvelRivalsPak();
        using var worker = new PakWorkerProcess();

        byte[]? read = null;
        var failure = await Record.ExceptionAsync(async () =>
        {
            using var reader = new PakReaderHandle(worker, pak, Key);
            await reader.OpenAsync(TestContext.Current.CancellationToken);
            using var bytes = await reader.ReadEntryAsync("Marvel/Content/Foo.uasset", TestContext.Current.CancellationToken);
            read = bytes.Memory.ToArray();
        });

        Assert.True(failure != null || !Contents.AsSpan().SequenceEqual(read), "a Marvel Rivals pak was read correctly as a standard one, so the variant proves nothing");
    }

    [Fact]
    public void AMarvelRivalsPak_OpensAsAnAssetSource_AndReadsItsHeader()
    {
        var pak = MarvelRivalsPak();

        using (var source = new PakAssetSource(pak, Key, variant: PakVariant.MarvelRivals))
        {
            Assert.Equal(PakVariant.MarvelRivals, source.Variant);
            Assert.Equal(["Marvel/Content/Foo.uasset"], source.ListAllEntries());
        }

        var header = PakMountPointReader.Read(pak, Key, PakVariant.MarvelRivals);
        Assert.Equal("../../../Marvel/", header.MountPoint);
        Assert.Equal(PakVersion.V11, header.Version);
    }

    [Fact]
    public void ARepackOfAMarvelRivalsPak_IsWrittenInTheMarvelRivalsFormat()
    {
        var pak = MarvelRivalsPak();
        var output = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_" + Guid.NewGuid() + ".pak");
        _files.Add(output);
        using (var source = new PakAssetSource(pak, Key, variant: PakVariant.MarvelRivals))
        {
            var result = PakRepacker.Build(source, output, compression: [PakCompression.Zlib], aesKey: Key);
            Assert.False(result.HasFailures);
            Assert.Equal(1, result.SucceededCount);
        }

        using var repacked = new PakAssetSource(output, Key, variant: PakVariant.MarvelRivals);
        using var bytes = repacked.ReadOriginalBytes("Marvel/Content/Foo.uasset");
        Assert.Equal(Contents, bytes.Memory.ToArray());
    }

    [Fact]
    public async Task ARepackOfAMarvelRivalsPak_IsNotReadAsAStandardOne()
    {
        var pak = MarvelRivalsPak();
        var output = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_" + Guid.NewGuid() + ".pak");
        _files.Add(output);
        using (var source = new PakAssetSource(pak, Key, variant: PakVariant.MarvelRivals))
            Assert.False(PakRepacker.Build(source, output, compression: [PakCompression.Zlib], aesKey: Key).HasFailures);
        using var worker = new PakWorkerProcess();

        byte[]? read = null;
        var failure = await Record.ExceptionAsync(async () =>
        {
            using var reader = new PakReaderHandle(worker, output, Key);
            await reader.OpenAsync(TestContext.Current.CancellationToken);
            using var bytes = await reader.ReadEntryAsync("Marvel/Content/Foo.uasset", TestContext.Current.CancellationToken);
            read = bytes.Memory.ToArray();
        });

        // The repack kept the variant: it is not a standard pak that merely happens to be encrypted.
        Assert.True(failure != null || !Contents.AsSpan().SequenceEqual(read));
    }
}
