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
    private const string SessionLostMessage = "The pak worker restarted while this pak was being written, so the new pak is incomplete.";

    private readonly PakWorkerClient _client;
    private long _sessionId;
    private bool _opened;

    public PakWriterHandle(PakWorkerProcess process) => _client = new PakWorkerClient(process);

    public async Task OpenAsync(
        string outputPakPath, string mountPoint, PakVersion version, PakCompression[]? compression, byte[]? aesKey,
        CancellationToken cancellationToken = default)
    {
        var request = new PakWorkerRequest
        {
            Opcode = PakWorkerOpcode.OpenWriter,
            PakPath = Path.GetFullPath(outputPakPath), // the worker's working folder is its own, so a relative path would resolve there
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

    public async Task WriteFileAsync(string entryPath, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (!_opened) throw new InvalidOperationException("PakWriterHandle.OpenAsync must be called first.");

        var request = new PakWorkerRequest { Opcode = PakWorkerOpcode.WriteFile, SessionId = _sessionId, EntryPath = entryPath };
        var (response, _) = await _client.SendAsync(request, bytes, operation: $"writing '{entryPath}'", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!response.Success)
            throw new InvalidOperationException(response.UnknownSession ? SessionLostMessage : response.Error ?? $"Failed to write '{entryPath}'.");
    }

    public async Task WriteIndexAsync(CancellationToken cancellationToken = default)
    {
        if (!_opened) throw new InvalidOperationException("PakWriterHandle.OpenAsync must be called first.");

        var request = new PakWorkerRequest { Opcode = PakWorkerOpcode.WriteIndex, SessionId = _sessionId };
        var (response, _) = await _client.SendAsync(request, operation: "finalizing pak index", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!response.Success)
            throw new InvalidOperationException(response.UnknownSession ? SessionLostMessage : response.Error ?? "Failed to finalize pak index.");
    }

    /// <summary>Closes the session without waiting on other callers' long requests, see <see cref="PakWorkerClient.FinishSoon"/>.</summary>
    public void Dispose()
    {
        if (!_opened) return;

        _opened = false;
        var request = new PakWorkerRequest { Opcode = PakWorkerOpcode.CloseWriter, SessionId = _sessionId };
        PakWorkerClient.FinishSoon(() => _client.SendAsync(request));
    }
}
