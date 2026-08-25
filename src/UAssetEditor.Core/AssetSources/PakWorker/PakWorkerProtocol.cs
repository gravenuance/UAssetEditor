using System.Buffers;
using System.Text;
using System.Text.Json;
using UAssetAPI;

namespace UAssetEditor.Core.AssetSources.PakWorker;

/// <summary>
/// A byte buffer rented from <see cref="ArrayPool{T}.Shared"/> by <see cref="PakWorkerFraming.ReadPooledMessageAsync{THeader}"/> -
/// used for pak entry bytes, which can be large enough (textures, audio) to land on the Large
/// Object Heap on every single read otherwise. <see cref="Length"/> is the real payload size;
/// the rented array itself is frequently larger (that's how <see cref="ArrayPool{T}"/> works),
/// so every consumer must go through <see cref="Span"/>/<see cref="Memory"/> rather than
/// assuming the backing array's own length is the payload's. Dispose returns the array to the
/// pool exactly once - do not read from a <see cref="RentedBuffer"/> after disposing it, and
/// do not dispose it twice.
/// </summary>
public readonly struct RentedBuffer : IDisposable, IEquatable<RentedBuffer>
{
    private readonly byte[] _array;

    public int Length { get; }

    public RentedBuffer(byte[] array, int length)
    {
        _array = array;
        Length = length;
    }

    public ReadOnlySpan<byte> Span => _array.AsSpan(0, Length);
    public ReadOnlyMemory<byte> Memory => _array.AsMemory(0, Length);

    /// <summary>Copies out an owned, exact-size array - for callers (tests, anything that outlives this buffer's Dispose) that can't work directly off a borrowed span/memory.</summary>
    public byte[] ToArray() => Span.ToArray();

    public void Dispose()
    {
        if (_array.Length > 0)
            ArrayPool<byte>.Shared.Return(_array);
    }

    // Identity, not content, equality (CA1815) - two RentedBuffers are "equal" only when they
    // wrap the exact same rented array at the exact same reported length, matching this type's
    // role as a handle onto pool-owned memory rather than a value type in its own right.
    public bool Equals(RentedBuffer other) => ReferenceEquals(_array, other._array) && Length == other.Length;
    public override bool Equals(object? obj) => obj is RentedBuffer other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_array, Length);
    public static bool operator ==(RentedBuffer left, RentedBuffer right) => left.Equals(right);
    public static bool operator !=(RentedBuffer left, RentedBuffer right) => !left.Equals(right);
}

/// <summary>Every operation the worker process can perform - see individual request DTOs for their arguments.</summary>
public enum PakWorkerOpcode
{
    OpenReader,
    ReadEntry,
    CloseReader,
    OpenWriter,
    WriteFile,
    WriteIndex,
    CloseWriter,
}

/// <summary>
/// One request, sent client (main app) -> server (worker). <see cref="PakPath"/>/
/// <see cref="AesKeyHex"/> are meaningful for both <see cref="PakWorkerOpcode.OpenReader"/>
/// and <see cref="PakWorkerOpcode.OpenWriter"/>; <see cref="EntryPath"/> for
/// <see cref="PakWorkerOpcode.ReadEntry"/>/<see cref="PakWorkerOpcode.WriteFile"/> (the
/// bytes to write for the latter travel in the accompanying payload frame, not here);
/// <see cref="MountPoint"/>/<see cref="Version"/>/<see cref="Compression"/> only for
/// <see cref="PakWorkerOpcode.OpenWriter"/>. A raw-bytes payload frame always follows the
/// JSON header (see <see cref="PakWorkerFraming"/>) even when empty, to keep the framing
/// code identical for every opcode.
/// </summary>
public sealed class PakWorkerRequest
{
    public required PakWorkerOpcode Opcode { get; init; }
    public int SessionId { get; init; }
    public string? PakPath { get; init; }
    public string? AesKeyHex { get; init; }
    public string? EntryPath { get; init; }
    public string? MountPoint { get; init; }
    public PakVersion? Version { get; init; }

    // CA1819 (properties shouldn't return arrays) doesn't apply here: this is an init-only
    // wire DTO built fresh per request and never shared/mutated after construction, and
    // UAssetAPI's own PakBuilder.Compression(PakCompression[]) demands an actual array on
    // the receiving end anyway - an IReadOnlyList<T> here would just mean converting back
    // to an array right before that call, for no benefit.
#pragma warning disable CA1819
    public PakCompression[]? Compression { get; init; }
#pragma warning restore CA1819
}

/// <summary>
/// One response, worker -> main app. <see cref="MountPoint"/>/<see cref="Version"/>/
/// <see cref="Entries"/> are only populated for a successful
/// <see cref="PakWorkerOpcode.OpenReader"/> response - fetched once up front so opening a pak
/// is one round trip instead of four. The entry's bytes for a successful
/// <see cref="PakWorkerOpcode.ReadEntry"/> travel in the accompanying raw-bytes payload
/// frame, not here.
/// </summary>
public sealed class PakWorkerResponse
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public int SessionId { get; init; }
    public string? MountPoint { get; init; }
    public PakVersion? Version { get; init; }
    public IReadOnlyList<string>? Entries { get; init; }
}

/// <summary>
/// The wire format shared by both sides of the pipe: one message is
/// <c>[4-byte LE length][UTF-8 JSON header][4-byte LE length][raw payload bytes]</c>. The
/// payload frame is always present (zero-length when an opcode has nothing to carry) so the
/// read/write code never has to branch per opcode. Kept deliberately minimal - this is
/// framing for a handful of request/response shapes over one already-serialized pipe, not a
/// general RPC framework.
/// </summary>
public static class PakWorkerFraming
{
    public static async Task WriteMessageAsync<THeader>(Stream stream, THeader header, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var json = JsonSerializer.SerializeToUtf8Bytes(header);
        await WriteFrameAsync(stream, json, cancellationToken).ConfigureAwait(false);
        await WriteFrameAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<(THeader Header, byte[] Payload)> ReadMessageAsync<THeader>(Stream stream, CancellationToken cancellationToken = default)
    {
        var json = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        var payload = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        var header = JsonSerializer.Deserialize<THeader>(json)
            ?? throw new InvalidDataException($"Malformed {typeof(THeader).Name} header: {Encoding.UTF8.GetString(json)}");
        return (header, payload);
    }

    /// <summary>
    /// Same wire format and header handling as <see cref="ReadMessageAsync{THeader}"/>, but
    /// the payload frame is rented from <see cref="ArrayPool{T}.Shared"/> instead of freshly
    /// allocated - for hot-path callers (reading many pak entries in a row) where the payload
    /// is discarded right after use and a fresh heap/LOH allocation per call is pure waste.
    /// The header frame is still a small, non-pooled array (not worth pooling something that
    /// size, and it needs UTF-8 decoding for the malformed-header error message anyway).
    /// </summary>
    public static async Task<(THeader Header, RentedBuffer Payload)> ReadPooledMessageAsync<THeader>(Stream stream, CancellationToken cancellationToken = default)
    {
        var json = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        var payload = await ReadPooledFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        var header = JsonSerializer.Deserialize<THeader>(json)
            ?? throw new InvalidDataException($"Malformed {typeof(THeader).Name} header: {Encoding.UTF8.GetString(json)}");
        return (header, payload);
    }

    private static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var lengthBytes = BitConverter.GetBytes(data.Length);
        await stream.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        if (data.Length > 0)
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = await ReadFrameLengthAsync(stream, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[length];
        await ReadExactAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    private static async Task<RentedBuffer> ReadPooledFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = await ReadFrameLengthAsync(stream, cancellationToken).ConfigureAwait(false);
        if (length == 0) return new RentedBuffer(Array.Empty<byte>(), 0);

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        await ReadExactAsync(stream, buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        return new RentedBuffer(buffer, length);
    }

    private static async Task<int> ReadFrameLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        await ReadExactAsync(stream, lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBytes);
        if (length < 0) throw new InvalidDataException($"Negative frame length: {length}");
        return length;
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Pak worker pipe closed mid-message.");
            read += n;
        }
    }
}
