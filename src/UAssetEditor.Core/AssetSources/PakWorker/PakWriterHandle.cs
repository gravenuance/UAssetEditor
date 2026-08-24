using UAssetAPI;

namespace UAssetEditor.Core.AssetSources.PakWorker;

/// <summary>
/// One new-pak-being-built session against the worker process. Unlike
/// <see cref="PakReaderHandle"/>, a crash here is NOT transparently recovered - a
/// <see cref="PakBuilder"/>'s write session is strictly sequential with no append/resume
/// capability (<see cref="WriteIndexAsync"/> finalizes once at the very end), so there is no
/// way to spin up a fresh worker and continue writing entries into an output file a dead
/// worker already partially wrote. Callers (<see cref="PakPacker"/>, <see cref="PakRepacker"/>)
/// catch a crash here, discard the partial output, and fail that whole attempt cleanly -
/// still a large improvement over today (one attempt fails instead of the whole app dying).
/// </summary>
public sealed class PakWriterHandle : IDisposable
{
    private readonly PakWorkerClient _client;
    private int _sessionId;
    private bool _opened;

    public PakWriterHandle(PakWorkerProcess process) => _client = new PakWorkerClient(process);

    public async Task OpenAsync(
        string outputPakPath, string mountPoint, PakVersion version, PakCompression[]? compression, byte[]? aesKey,
        CancellationToken cancellationToken = default)
    {
        var request = new PakWorkerRequest
        {
            Opcode = PakWorkerOpcode.OpenWriter,
            PakPath = outputPakPath,
            MountPoint = mountPoint,
            Version = version,
            Compression = compression,
            AesKeyHex = aesKey != null ? Convert.ToHexString(aesKey) : null,
        };

        var (response, _) = await _client.SendAsync(request, operation: $"creating '{outputPakPath}'", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? $"Failed to create '{outputPakPath}'.");

        _sessionId = response.SessionId;
        _opened = true;
    }

    public async Task WriteFileAsync(string entryPath, byte[] bytes, CancellationToken cancellationToken = default)
    {
        if (!_opened) throw new InvalidOperationException("PakWriterHandle.OpenAsync must be called first.");

        var request = new PakWorkerRequest { Opcode = PakWorkerOpcode.WriteFile, SessionId = _sessionId, EntryPath = entryPath };
        var (response, _) = await _client.SendAsync(request, bytes, operation: $"writing '{entryPath}'", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? $"Failed to write '{entryPath}'.");
    }

    public async Task WriteIndexAsync(CancellationToken cancellationToken = default)
    {
        if (!_opened) throw new InvalidOperationException("PakWriterHandle.OpenAsync must be called first.");

        var request = new PakWorkerRequest { Opcode = PakWorkerOpcode.WriteIndex, SessionId = _sessionId };
        var (response, _) = await _client.SendAsync(request, operation: "finalizing pak index", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "Failed to finalize pak index.");
    }

    public void Dispose()
    {
        if (!_opened) return;

        try
        {
            var request = new PakWorkerRequest { Opcode = PakWorkerOpcode.CloseWriter, SessionId = _sessionId };
            _client.SendAsync(request).GetAwaiter().GetResult();
        }
        catch
        {
            // Best effort - a worker that's already gone has nothing left to close.
        }
    }
}
