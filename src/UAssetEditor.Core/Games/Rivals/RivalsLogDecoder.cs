using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace UAssetEditor.Core.Games.Rivals;

/// <summary>What a decode run found. <see cref="StoppedAtOffset"/> is set when the file ended in something that is not a whole record.</summary>
/// <param name="Records">Records decoded.</param>
/// <param name="InflateFailures">Compressed records whose zlib data was corrupt; what inflated before the error was still emitted.</param>
/// <param name="StoppedAtOffset">File offset of the first byte that could not be read as a record, or null when the log ended cleanly (or in the SHM trailer).</param>
/// <param name="HasTrailer">Whether the plaintext shared-memory trailer was found and emitted.</param>
public sealed record RivalsLogDecodeResult(int Records, int InflateFailures, int? StoppedAtOffset, bool HasTrailer);

/// <summary>
/// Decodes a Marvel Rivals engine log (<c>%LOCALAPPDATA%\Marvel\Saved\Logs\*.log</c>, the <c>logversion000</c> container) to text.
/// Format and key from oZanderr/Rivals-Toolkit <c>crates/rivals-core/src/game_logs/{container,decode}.rs</c>: records framed as
/// i32 size, payload, i32 size; each payload AES-128-CTR (counter = IV, big-endian, restarting at the IV per record), then zlib
/// when it starts with 0x78; an optional plaintext trailer after <c>&lt;SHM_LOG_BEGIN&gt;</c>.
/// </summary>
public static class RivalsLogDecoder
{
    private const int BlockSize = 16;
    private const byte ZlibHeader = 0x78;

    private static ReadOnlySpan<byte> Magic => "logversion000"u8;

    private static ReadOnlySpan<byte> ShmBegin => "<SHM_LOG_BEGIN>"u8;

    private static ReadOnlySpan<byte> TrailerHeading => "\n----- SHM trailer -----\n"u8;

    private static ReadOnlySpan<byte> Key => "ezFAgrXnhBOZgFFD"u8;

    private static ReadOnlySpan<byte> Iv => "_NMxNi0DK7_qHC_i"u8;

    /// <summary>Decodes <paramref name="blob"/> into <paramref name="output"/>, record by record.</summary>
    /// <exception cref="InvalidDataException"><paramref name="blob"/> is not a <c>logversion000</c> log.</exception>
    public static RivalsLogDecodeResult Decode(ReadOnlyMemory<byte> blob, Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return Decode(blob, chunk =>
        {
            output.Write(chunk.Span);
            return true;
        });
    }

    /// <summary>
    /// Decodes <paramref name="blob"/> and hands each decoded piece to <paramref name="onChunk"/>, which returns false to stop
    /// early (a caller that only wants the header lines).
    /// </summary>
    /// <exception cref="InvalidDataException"><paramref name="blob"/> is not a <c>logversion000</c> log.</exception>
    public static RivalsLogDecodeResult Decode(ReadOnlyMemory<byte> blob, Func<ReadOnlyMemory<byte>, bool> onChunk)
    {
        ArgumentNullException.ThrowIfNull(onChunk);
        var data = blob.Span;
        if (!data.StartsWith(Magic)) throw new InvalidDataException("Not a logversion000 log.");

        using var aes = Aes.Create();
#pragma warning disable CA5390 // the game's own fixed log key, published by Rivals-Toolkit; nothing of ours is protected by it
        aes.Key = Key.ToArray();
#pragma warning restore CA5390

        var offset = Magic.Length;
        int records = 0, inflateFailures = 0;
        while (offset + 4 <= data.Length)
        {
            var window = Math.Min(data.Length - offset, 8 + ShmBegin.Length);
            var shm = data.Slice(offset, window).IndexOf(ShmBegin);
            if (shm >= 0) return Finish(WriteTrailer(blob, offset + shm, onChunk));

            var size = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
            if (size < 0 || (long)offset + 8 + size > data.Length || BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4 + size)..]) != size)
            {
                var at = data[offset..].IndexOf(ShmBegin);
                return at >= 0
                    ? Finish(WriteTrailer(blob, offset + at, onChunk))
                    : new RivalsLogDecodeResult(records, inflateFailures, offset, HasTrailer: false);
            }

            var plain = Transform(aes, data.Slice(offset + 4, size));
            if (plain.Length > 0 && plain[0] == ZlibHeader)
            {
                var (inflated, ok) = Inflate(plain);
                if (!ok) inflateFailures++;
                plain = inflated;
            }
            records++;
            offset += 8 + size;
            if (!onChunk(plain)) return new RivalsLogDecodeResult(records, inflateFailures, StoppedAtOffset: null, HasTrailer: false);
        }

        // One to three bytes cannot hold a record's size word: the file was cut off.
        return new RivalsLogDecodeResult(records, inflateFailures, offset < data.Length ? offset : null, HasTrailer: false);

        RivalsLogDecodeResult Finish(bool trailerEmitted) => new(records, inflateFailures, StoppedAtOffset: null, HasTrailer: trailerEmitted);
    }

    /// <summary>Adds one to a 16-byte big-endian counter, wrapping to zero after all 0xFF.</summary>
    internal static void IncrementCounter(Span<byte> counter)
    {
        for (var i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0) return;
        }
    }

    private static bool WriteTrailer(ReadOnlyMemory<byte> blob, int markerAt, Func<ReadOnlyMemory<byte>, bool> onChunk)
    {
        if (!onChunk(TrailerHeading.ToArray())) return false;
        onChunk(blob[(markerAt + ShmBegin.Length)..]);
        return true;
    }

    // CTR keystream = AES-ECB of the counter blocks, so one ECB call covers the whole record.
    private static byte[] Transform(Aes aes, ReadOnlySpan<byte> data)
    {
        var result = new byte[data.Length];
        if (data.Length == 0) return result;

        var counters = new byte[(data.Length + BlockSize - 1) / BlockSize * BlockSize];
        Span<byte> counter = stackalloc byte[BlockSize];
        Iv.CopyTo(counter);
        for (var at = 0; at < counters.Length; at += BlockSize)
        {
            counter.CopyTo(counters.AsSpan(at));
            IncrementCounter(counter);
        }

        var keystream = aes.EncryptEcb(counters, PaddingMode.None);
        for (var i = 0; i < data.Length; i++) result[i] = (byte)(data[i] ^ keystream[i]);
        return result;
    }

    // Keeps whatever inflated before corrupt data: a damaged record still shows its first lines.
    private static (byte[] Data, bool Ok) Inflate(byte[] data)
    {
        using var zlib = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        try
        {
            zlib.CopyTo(inflated);
            return (inflated.ToArray(), true);
        }
        catch (InvalidDataException)
        {
            return (inflated.ToArray(), false);
        }
    }
}
