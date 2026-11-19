using System.Text;
using UAssetAPI;

namespace UAssetEditor.Core.Tests;

/// <summary>
/// UAssetAPI's managed side of the native repak_bind boundary (string marshalling, handle lifetime, stream callbacks, error text),
/// driven in this process through the public PakBuilder/PakWriter/PakReader.
/// </summary>
[Collection("Pak")]
public sealed class PakInteropTests
{
    private static readonly byte[] Contents = Encoding.ASCII.GetBytes("some file contents");

    private static MemoryStream WritePak(params string[] names)
    {
        var stream = new MemoryStream();
        using var builder = new PakBuilder();
        using var writer = builder.Writer(stream);
        foreach (var name in names) writer.WriteFile(name, Encoding.UTF8.GetBytes("contents of " + name));
        writer.WriteIndex();
        return stream;
    }

    [Fact]
    public void EntryNamesWithNonAsciiCharacters_RoundTrip()
    {
        string[] names = ["é.txt", "日本語/ファイル.txt", "plain.txt"];
        using var stream = WritePak(names);

        using var builder = new PakBuilder();
        using var reader = builder.Reader(stream);

        Assert.Equal(names.Order(StringComparer.Ordinal), reader.Files().Order(StringComparer.Ordinal));
        foreach (var name in names)
            Assert.Equal("contents of " + name, Encoding.UTF8.GetString(reader.Get(stream, name)));
    }

    [Fact]
    public void AnInteriorNulInAnEntryName_IsRefused()
    {
        using var stream = new MemoryStream();
        using var builder = new PakBuilder();
        using var writer = builder.Writer(stream);

        // Marshalling would cut the name at the NUL and write "b" instead.
        Assert.Throws<ArgumentException>(() => writer.WriteFile("b\0c.txt", Contents));
    }

    [Fact]
    public void AnInteriorNulInAMountPoint_IsRefused()
    {
        using var builder = new PakBuilder();

        Assert.Throws<ArgumentException>(() => builder.Writer(new MemoryStream(), PakVersion.V11, "../../../a\0b/"));
    }

    [Fact]
    public void AnInteriorNulInAnEntryPathToRead_IsRefused()
    {
        using var stream = WritePak("b.txt");
        using var builder = new PakBuilder();
        using var reader = builder.Reader(stream);

        // Cut at the NUL this would read "b.txt".
        Assert.Throws<ArgumentException>(() => reader.Get(stream, "b.txt\0ignored"));
    }

    [Fact]
    public void ALateWriteIndexOrWriteFile_IsRefusedAsAnInvalidHandle()
    {
        using var stream = new MemoryStream();
        using var builder = new PakBuilder();
        using var writer = builder.Writer(stream);
        writer.WriteIndex();

        Assert.Throws<InvalidOperationException>(() => writer.WriteIndex());
        Assert.Throws<InvalidOperationException>(() => writer.WriteFile("late.txt", Contents));
    }

    [Fact]
    public void ANonPakStream_FailsWithTheNativeReason()
    {
        using var builder = new PakBuilder();
        using var stream = new MemoryStream(new byte[300]);

        var error = Assert.ThrowsAny<Exception>(() => builder.Reader(stream));

        // repak's text for a footer without the pak magic (0x5A6F12E1).
        Assert.Contains("0x5a6f12e1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReadOfAMissingEntry_FailsWithTheNativeReason()
    {
        using var stream = WritePak("here.txt");
        using var builder = new PakBuilder();
        using var reader = builder.Reader(stream);

        var error = Assert.ThrowsAny<Exception>(() => reader.Get(stream, "missing.txt"));

        Assert.Contains("No entry found at missing.txt", error.Message, StringComparison.Ordinal);
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("disk full");
    }

    [Fact]
    public void AStreamThatThrowsOnWrite_ReportsItsExceptionAsTheInnerOne()
    {
        using var stream = new FailingWriteStream();
        using var builder = new PakBuilder();
        using var writer = builder.Writer(stream);

        var error = Assert.ThrowsAny<Exception>(() => writer.WriteFile("a.txt", [1, 2, 3]));

        var inner = Assert.IsType<IOException>(error.InnerException);
        Assert.Equal("disk full", inner.Message);
    }

    private sealed class ThrowingReadStream() : MemoryStream(new byte[300])
    {
        public override int Read(Span<byte> buffer) => throw new NotSupportedException("no reads");
    }

    [Fact]
    public void AStreamThatThrowsOnRead_ReportsItsExceptionAsTheInnerOne()
    {
        using var builder = new PakBuilder();

        var error = Assert.ThrowsAny<Exception>(() => builder.Reader(new ThrowingReadStream()));

        Assert.IsType<NotSupportedException>(error.InnerException);
    }

    private sealed class FailsDifferentlyStream : MemoryStream
    {
        private int _writes;

        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw (_writes++ == 0 ? new IOException("disk full") : new InvalidOperationException("closed"));
    }

    [Fact]
    public void ALaterFailure_DoesNotCarryAnEarlierStreamException()
    {
        using var stream = new FailsDifferentlyStream();
        using var builder = new PakBuilder();
        using var writer = builder.Writer(stream);

        var first = Assert.ThrowsAny<Exception>(() => writer.WriteFile("a.txt", [1, 2, 3]));
        Assert.IsType<IOException>(first.InnerException);
        var later = Assert.ThrowsAny<Exception>(() => writer.WriteFile("b.txt", [1]));
        Assert.IsType<InvalidOperationException>(later.InnerException);
    }

    [Fact]
    public void Lz4_RoundTrips()
    {
        // repak's Compression::LZ4 is the fifth variant of a #[repr(u8)] enum.
        Assert.Equal(4, (byte)PakCompression.LZ4);
        var contents = Encoding.ASCII.GetBytes(new string('a', 1000));
        using var stream = new MemoryStream();
        using (var builder = new PakBuilder())
        {
            builder.Compression([PakCompression.LZ4]);
            using var writer = builder.Writer(stream);
            writer.WriteFile("lz4.txt", contents);
            writer.WriteIndex();
        }

        using var readBuilder = new PakBuilder();
        using var reader = readBuilder.Reader(stream);
        Assert.Equal(contents, reader.Get(stream, "lz4.txt"));
    }

    private static readonly byte[] MarvelKey = Enumerable.Repeat((byte)7, 32).ToArray();

    [Fact]
    public void AMarvelRivalsPak_RoundTripsWithTheMarvelRivalsVariant()
    {
        var contents = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("some file contents", 100)));
        using var stream = new MemoryStream();
        using (var builder = new PakBuilder())
        {
            builder.Key(MarvelKey).Variant(PakVariant.MarvelRivals);
            using var writer = builder.Writer(stream);
            writer.WriteFile("a/file.txt", contents);
            writer.WriteIndex();
        }

        using var readBuilder = new PakBuilder();
        readBuilder.Key(MarvelKey).Variant(PakVariant.MarvelRivals);
        using var reader = readBuilder.Reader(stream);
        Assert.Equal(contents, reader.Get(stream, "a/file.txt"));
    }

    [Fact]
    public void AnUnknownVariant_IsRefusedWithTheNativeReason()
    {
        using var builder = new PakBuilder();

        var error = Assert.Throws<RePakException>(() => builder.Variant((PakVariant)99));

        Assert.Contains("99 is not a pak variant", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCompression_IsRefusedWithTheNativeReason()
    {
        using var builder = new PakBuilder();

        var error = Assert.ThrowsAny<Exception>(() => builder.Compression([PakCompression.Zlib, (PakCompression)99]));

        Assert.Contains("99 is not a compression method", error.Message, StringComparison.Ordinal);
    }
}
