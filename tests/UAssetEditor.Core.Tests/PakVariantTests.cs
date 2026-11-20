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
    // The key the game's own paks are encrypted with: the profile key with each 4-byte word reversed (hand-derived).
    private static readonly byte[] PakCipherKey = Convert.FromHexString("8C3D260C85B0DC22C3994889836379A3E09DBFE9C908FBCB2EEF2DBF749DF284");
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
        builder.Key(PakCipherKey).Variant(PakVariant.MarvelRivals).Compression([PakCompression.Zlib]);
        using var writer = builder.Writer(stream, PakVersion.V11, "../../../Marvel/");
        writer.WriteFile("Marvel/Content/Foo.uasset", Contents);
        writer.WriteIndex();
        return path;
    }

    /// <summary>Reads an entry the way a plain repak reader does: the pak cipher key is given directly, with no profile key in sight.</summary>
    private static byte[] ReadWithAPlainReader(string pak, string entry)
    {
        using var stream = File.OpenRead(pak);
        using var builder = new PakBuilder();
        builder.Key(PakCipherKey).Variant(PakVariant.MarvelRivals);
        using var reader = builder.Reader(stream);
        return reader.Get(stream, entry);
    }

    [Fact]
    public void APakPackedForMarvelRivals_IsReadableByAPlainReader_GivenTheReversedKey()
    {
        var folder = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Pack_" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "Foo.uasset"), Contents);
        var output = folder + ".pak";
        _files.Add(output);
        try
        {
            var result = PakPacker.Build(folder, output, "../../../Marvel/", compression: [PakCompression.Zlib], aesKey: Key, variant: PakVariant.MarvelRivals,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(result.HasFailures);
            Assert.Equal(Contents, ReadWithAPlainReader(output, "Foo.uasset"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ARepackOfAMarvelRivalsPak_IsReadableByAPlainReader_GivenTheReversedKey()
    {
        var pak = MarvelRivalsPak();
        var output = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_" + Guid.NewGuid() + ".pak");
        _files.Add(output);
        using (var source = new PakAssetSource(pak, Key, variant: PakVariant.MarvelRivals))
            Assert.False(PakRepacker.Build(source, output, compression: [PakCompression.Zlib]).HasFailures);

        Assert.Equal(Contents, ReadWithAPlainReader(output, "Marvel/Content/Foo.uasset"));
    }

    [Fact]
    public void AMarvelRivalsPak_WrittenWithTheProfileKeyAsStored_IsNotWhatTheGameReads()
    {
        // The pre-fix writer used the profile key as is; the game cannot read such a pak, so neither may we call it a Rivals pak.
        var path = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_" + Guid.NewGuid() + ".pak");
        _files.Add(path);
        using (var stream = File.Create(path))
        using (var builder = new PakBuilder())
        {
            builder.Key(Key).Variant(PakVariant.MarvelRivals).Compression([PakCompression.Zlib]);
            using var writer = builder.Writer(stream, PakVersion.V11, "../../../Marvel/");
            writer.WriteFile("Marvel/Content/Foo.uasset", Contents);
            writer.WriteIndex();
        }

        // Control: the pak itself is sound and the native reader works; only the key form decides.
        using (var stream = File.OpenRead(path))
        using (var asStored = new PakBuilder())
        {
            asStored.Key(Key).Variant(PakVariant.MarvelRivals);
            using var reader = asStored.Reader(stream);
            Assert.Equal(Contents, reader.Get(stream, "Marvel/Content/Foo.uasset"));
        }

        Assert.Throws<IOException>(() => ReadWithAPlainReader(path, "Marvel/Content/Foo.uasset"));
        Assert.ThrowsAny<Exception>(() => PakMountPointReader.Read(path, Key, PakVariant.MarvelRivals));
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
    public void AnUnknownCompression_IsRefusedWithEveryCodecTheLibraryKnows()
    {
        var error = Assert.Throws<ArgException>(() => AssetIo.ResolveCompression(new ArgReader(["--compression", "rot13"])));

        foreach (var codec in Enum.GetNames<PakCompression>())
            Assert.Contains(codec, error.Message, StringComparison.Ordinal);
        Assert.Equal([PakCompression.LZ4], AssetIo.ResolveCompression(new ArgReader(["--compression", "lz4"])));
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
            var result = PakRepacker.Build(source, output, compression: [PakCompression.Zlib]);
            Assert.False(result.HasFailures);
            Assert.Equal(1, result.SucceededCount);
        }

        using var repacked = new PakAssetSource(output, Key, variant: PakVariant.MarvelRivals);
        using var bytes = repacked.ReadOriginalBytes("Marvel/Content/Foo.uasset");
        Assert.Equal(Contents, bytes.Memory.ToArray());
    }

    [Fact]
    public void AnOpenSource_KnowsTheFormatItWasOpenedWith_ToCompareWithTheCurrentSettings()
    {
        var pak = MarvelRivalsPak();
        using var source = new PakAssetSource(pak, Key, variant: PakVariant.MarvelRivals);

        Assert.Equal(PakFormat.Of(Key, PakVariant.MarvelRivals), source.Format);
        Assert.Equal(source.Format, PakFormat.Resolve(GameProfile.For(Game.MarvelRivals), GameProfile.For(Game.MarvelRivals)!.AesKeyHex));
        Assert.Equal(source.Format, PakFormat.Resolve(null, GameProfile.For(Game.MarvelRivals)!.AesKeyHex, source.Variant));
        Assert.NotEqual(source.Format, PakFormat.Resolve(GameProfile.For(Game.FinalFantasy7Remake), GameProfile.For(Game.FinalFantasy7Remake)!.AesKeyHex));
        Assert.NotEqual(source.Format, PakFormat.Resolve(GameProfile.For(Game.MarvelRivals), "00"));
    }

    [Fact]
    public void ARepack_IsWrittenWithTheKeyTheSourceWasOpenedWith_WithoutTheCallerPassingItAgain()
    {
        var pak = MarvelRivalsPak();
        var output = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_" + Guid.NewGuid() + ".pak");
        _files.Add(output);
        using (var source = new PakAssetSource(pak, Key, variant: PakVariant.MarvelRivals))
            Assert.False(PakRepacker.Build(source, output, compression: [PakCompression.Zlib]).HasFailures);

        byte[]? readWithoutKey = null;
        var withoutKey = Record.Exception(() =>
        {
            using var unkeyed = new PakAssetSource(output, null, variant: PakVariant.MarvelRivals);
            using var bytes = unkeyed.ReadOriginalBytes("Marvel/Content/Foo.uasset");
            readWithoutKey = bytes.Memory.ToArray();
        });
        using var keyed = new PakAssetSource(output, Key, variant: PakVariant.MarvelRivals);
        using var read = keyed.ReadOriginalBytes("Marvel/Content/Foo.uasset");

        Assert.True(withoutKey != null || !Contents.AsSpan().SequenceEqual(readWithoutKey), "the repack is readable without the key, so it was not encrypted");
        Assert.Equal(Contents, read.Memory.ToArray());
    }

    [Fact]
    public async Task ARepackOfAMarvelRivalsPak_IsNotReadAsAStandardOne()
    {
        var pak = MarvelRivalsPak();
        var output = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_" + Guid.NewGuid() + ".pak");
        _files.Add(output);
        using (var source = new PakAssetSource(pak, Key, variant: PakVariant.MarvelRivals))
            Assert.False(PakRepacker.Build(source, output, compression: [PakCompression.Zlib]).HasFailures);
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
