using UAssetAPI;

namespace UAssetEditor.Core.AssetSources.PakWorker;

/// <summary>
/// One open-pak session against the worker process. Reading an entry that crashes the
/// worker (the confirmed real-world repro: one specific entry in a large pak) transparently
/// respawns the worker and replays <see cref="OpenAsync"/> before rethrowing that one
/// entry's failure - so the *next* read, of a different entry, works normally without the
/// caller having to reopen the pak by hand. This is the reader-side half of the
/// respawn-and-reopen story; <c>PakWriterHandle</c> (Phase 2) cannot offer the same
/// guarantee, since a pak writer session has no append/resume capability.
/// </summary>
public sealed class PakReaderHandle : IDisposable
{
    private readonly PakWorkerProcess _process;
    private readonly PakWorkerClient _client;
    private readonly string _pakPath;
    private readonly byte[]? _aesKey;
    private int _sessionId;
    private bool _opened;

    public string MountPoint { get; private set; } = "";
    public PakVersion Version { get; private set; }
    public IReadOnlyList<string> Entries { get; private set; } = Array.Empty<string>();

    public PakReaderHandle(PakWorkerProcess process, string pakPath, byte[]? aesKey)
    {
        _process = process;
        _client = new PakWorkerClient(process);
        _pakPath = pakPath;
        _aesKey = aesKey;
    }

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        var request = new PakWorkerRequest
        {
            Opcode = PakWorkerOpcode.OpenReader,
            PakPath = _pakPath,
            AesKeyHex = _aesKey != null ? Convert.ToHexString(_aesKey) : null,
        };

        var (response, _) = await _client.SendAsync(request, operation: $"opening '{_pakPath}'", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? $"Failed to open '{_pakPath}'.");

        _sessionId = response.SessionId;
        MountPoint = response.MountPoint ?? "";
        Version = response.Version ?? PakVersion.V11;
        Entries = response.Entries ?? Array.Empty<string>();
        _opened = true;
    }

    public async Task<byte[]> ReadEntryAsync(string entryPath, CancellationToken cancellationToken = default)
    {
        if (!_opened) throw new InvalidOperationException("PakReaderHandle.OpenAsync must be called first.");

        try
        {
            var request = new PakWorkerRequest { Opcode = PakWorkerOpcode.ReadEntry, SessionId = _sessionId, EntryPath = entryPath };
            var (response, payload) = await _client.SendAsync(request, operation: $"reading '{entryPath}'", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!response.Success)
                throw new InvalidOperationException(response.Error ?? $"Failed to read '{entryPath}'.");
            return payload;
        }
        catch (PakWorkerCrashedException)
        {
            // Reopen eagerly so the pak stays usable for whatever the caller (or the next
            // caller) reads next - only this one entry's read is lost.
            await OpenAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose()
    {
        if (!_opened || _process.IsDead) return;

        try
        {
            var request = new PakWorkerRequest { Opcode = PakWorkerOpcode.CloseReader, SessionId = _sessionId };
            _client.SendAsync(request).GetAwaiter().GetResult();
        }
        catch
        {
            // Best effort - a worker that's already gone has nothing left to close.
        }
    }
}
