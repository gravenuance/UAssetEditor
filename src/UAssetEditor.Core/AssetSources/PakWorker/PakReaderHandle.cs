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
    private readonly Func<string, OodleLookup> _locateOodle;
    private int _sessionId;
    private int _generation;
    private bool _opened;

    public string MountPoint { get; private set; } = "";
    public PakVersion Version { get; private set; }
    public IReadOnlyList<string> Entries { get; private set; } = Array.Empty<string>();

    public PakReaderHandle(PakWorkerProcess process, string pakPath, byte[]? aesKey)
        : this(process, pakPath, aesKey, static path => OodleLibrary.Locate(path))
    {
    }

    /// <summary><paramref name="locateOodle"/> finds the pak's Oodle DLL, only once a read has failed for want of one (a test seam: the real lookup depends on the machine).</summary>
    internal PakReaderHandle(PakWorkerProcess process, string pakPath, byte[]? aesKey, Func<string, OodleLookup> locateOodle)
    {
        _process = process;
        _client = new PakWorkerClient(process);
        _pakPath = pakPath;
        _aesKey = aesKey;
        _locateOodle = locateOodle;
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
        _generation = _process.Generation;
        MountPoint = response.MountPoint ?? "";
        Version = response.Version ?? PakVersion.V11;
        Entries = response.Entries ?? Array.Empty<string>();
        _opened = true;
    }

    /// <summary>Returns a pooled buffer (see <see cref="RentedBuffer"/>) - callers must dispose it once they're done with the entry's bytes.</summary>
    public async Task<RentedBuffer> ReadEntryAsync(string entryPath, CancellationToken cancellationToken = default)
    {
        if (!_opened) throw new InvalidOperationException("PakReaderHandle.OpenAsync must be called first.");

        try
        {
            // A worker replaced for another reader (or after a crash) has no trace of this session.
            await _process.EnsureAsync(cancellationToken).ConfigureAwait(false);
            if (_process.Generation != _generation)
                await OpenAsync(cancellationToken).ConfigureAwait(false);

            var (response, payload) = await ReadOnceAsync(entryPath, cancellationToken).ConfigureAwait(false);
            if (response.Success) return payload;
            if (!OodleLibrary.IsMissingError(response.Error))
                throw new InvalidOperationException(response.Error ?? $"Failed to read '{entryPath}'.");

            return await ReadWithFoundOodleAsync(entryPath, response.Error, cancellationToken).ConfigureAwait(false);
        }
        catch (PakWorkerCrashedException)
        {
            // Reopen eagerly so the pak stays usable for whatever the caller (or the next
            // caller) reads next - only this one entry's read is lost.
            await OpenAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>One read; on failure the response carries the error and the buffer is already returned to the pool.</summary>
    private async Task<(PakWorkerResponse Response, RentedBuffer Payload)> ReadOnceAsync(string entryPath, CancellationToken cancellationToken)
    {
        var request = new PakWorkerRequest { Opcode = PakWorkerOpcode.ReadEntry, SessionId = _sessionId, EntryPath = entryPath };
        var (response, payload) = await _client.SendPooledAsync(request, operation: $"reading '{entryPath}'", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!response.Success)
        {
            // A failure response never actually carries a payload today, but don't rely
            // on that staying true - whatever was rented here still needs to go back.
            payload.Dispose();
        }

        return (response, payload);
    }

    /// <summary>
    /// The worker has no usable Oodle DLL, so look for one near the pak now (not on every open: most paks never need it).
    /// A DLL the worker does not hold yet goes to a replacement worker and the read is made once more; otherwise
    /// the failure is explained.
    /// </summary>
    private async Task<RentedBuffer> ReadWithFoundOodleAsync(string entryPath, string? firstError, CancellationToken cancellationToken)
    {
        var oodle = _locateOodle(_pakPath);
        var error = firstError;
        if (oodle.Path != null && await _process.UseOodleLibraryAsync(oodle.Path, _generation, cancellationToken).ConfigureAwait(false))
        {
            await _process.EnsureAsync(cancellationToken).ConfigureAwait(false);
            await OpenAsync(cancellationToken).ConfigureAwait(false);

            var (response, payload) = await ReadOnceAsync(entryPath, cancellationToken).ConfigureAwait(false);
            if (response.Success) return payload;
            error = response.Error;
        }

        throw new InvalidOperationException(OodleLibrary.Explain(error, oodle) ?? error ?? $"Failed to read '{entryPath}'.");
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
