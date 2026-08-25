using System.Text;
using System.Text.Json;
using UAssetAPI;

namespace UAssetEditor.Core.AssetSources.PakWorker;

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

    private static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var lengthBytes = BitConverter.GetBytes(data.Length);
        await stream.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        if (data.Length > 0)
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        await ReadExactAsync(stream, lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBytes);
        if (length < 0) throw new InvalidDataException($"Negative frame length: {length}");

        var buffer = new byte[length];
        await ReadExactAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Pak worker pipe closed mid-message.");
            read += n;
        }
    }
}
