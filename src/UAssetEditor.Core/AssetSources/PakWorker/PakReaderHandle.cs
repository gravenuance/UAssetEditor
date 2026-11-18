using Microsoft.Extensions.Logging;
using UAssetAPI;
using UAssetEditor.Core.Logging;

namespace UAssetEditor.Core.AssetSources.PakWorker;

/// <summary>
/// One open-pak session against a pak worker. Reading an entry that crashes the
/// worker (the confirmed real-world repro: one specific entry in a large pak) transparently
/// respawns the worker and replays <see cref="OpenAsync"/> before rethrowing that one
/// entry's failure - so the *next* read, of a different entry, works normally without the
/// caller having to reopen the pak by hand. A read that finds its session gone (the worker restarted
/// for someone else's crash) reopens the pak and reads again. This is the reader-side half of the
/// respawn-and-reopen story; <c>PakWriterHandle</c> (Phase 2) cannot offer the same
/// guarantee, since a pak writer session has no append/resume capability.
/// <para>
/// A read that fails for want of an Oodle DLL looks for one near the pak and, when it finds one the worker does not
/// hold, moves this reader (and only it) to the worker that does, see <see cref="PakWorkerProcess.ForOodleLibrary"/>.
/// </para>
/// <para>
/// The worker, the session id and the open state change together under one lock, so reads, reopens, moves and
/// <see cref="Dispose"/> on one handle never see half of an update.
/// </para>
/// </summary>
public sealed partial class PakReaderHandle : IDisposable
{
    private readonly string _pakPath;
    private readonly byte[]? _aesKey;
    private readonly Func<string, OodleLookup> _locateOodle;
#pragma warning disable CA2213 // holds no OS handle (AvailableWaitHandle is never used); disposing it would break a caller still waiting on it
    private readonly SemaphoreSlim _gate = new(1, 1);
#pragma warning restore CA2213
    private PakWorkerProcess _process;
    private PakWorkerClient _client;
    private OodleLookup? _oodle;
    private long _sessionId;
    private bool _opened;

    /// <summary>The session died with a worker and has not been reopened yet.</summary>
    private bool _sessionLost;

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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await OpenLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OpenLockedAsync(CancellationToken cancellationToken)
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
        _sessionLost = false;
        MountPoint = response.MountPoint ?? "";
        Version = response.Version ?? PakVersion.V11;
        Entries = response.Entries ?? Array.Empty<string>();
        _opened = true;
    }

    /// <summary>Returns a pooled buffer (see <see cref="RentedBuffer"/>) - callers must dispose it once they're done with the entry's bytes.</summary>
    public async Task<RentedBuffer> ReadEntryAsync(string entryPath, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_opened) throw new InvalidOperationException("PakReaderHandle.OpenAsync must be called first.");

            return await ReadLockedAsync(entryPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RentedBuffer> ReadLockedAsync(string entryPath, CancellationToken cancellationToken)
    {
        try
        {
            if (_sessionLost)
                await OpenLockedAsync(cancellationToken).ConfigureAwait(false);

            var (response, payload) = await ReadReopeningAsync(entryPath, cancellationToken).ConfigureAwait(false);
            if (response.Success) return payload;
            if (!OodleLibrary.IsMissingError(response.Error))
                throw new InvalidOperationException(response.Error ?? $"Failed to read '{entryPath}'.");

            return await ReadWithFoundOodleAsync(entryPath, response.Error, cancellationToken).ConfigureAwait(false);
        }
        catch (PakWorkerCrashedException)
        {
            // Reopen eagerly so the pak stays usable for whatever the caller (or the next
            // caller) reads next - only this one entry's read is lost. A reopen that fails
            // must not replace the crash the caller needs to see; the next read reopens.
            _sessionLost = true;
            try
            {
                await OpenLockedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception reopenFailure) when (reopenFailure is not OperationCanceledException)
            {
                LogReopenFailed(AppLog.For<PakReaderHandle>(), _pakPath, reopenFailure);
            }

            throw;
        }
    }

    /// <summary>One read; a session the worker no longer knows (it restarted for someone else's crash) is reopened and read again once.</summary>
    private async Task<(PakWorkerResponse Response, RentedBuffer Payload)> ReadReopeningAsync(string entryPath, CancellationToken cancellationToken)
    {
        var result = await ReadOnceAsync(entryPath, cancellationToken).ConfigureAwait(false);
        if (result.Response.Success || !result.Response.UnknownSession) return result;

        await OpenLockedAsync(cancellationToken).ConfigureAwait(false);
        return await ReadOnceAsync(entryPath, cancellationToken).ConfigureAwait(false);
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
    /// The worker has no usable Oodle DLL, so look for one near the pak now (not on every open: most paks never need it;
    /// once per reader, since a failed search would repeat for every entry). A DLL held by another worker moves this
    /// reader there and the read is made once more; otherwise, or when that fails too, the failure is explained.
    /// </summary>
    private async Task<RentedBuffer> ReadWithFoundOodleAsync(string entryPath, string? firstError, CancellationToken cancellationToken)
    {
        var oodle = _oodle ??= _locateOodle(_pakPath);
        var error = firstError;
        var worker = oodle.Path != null ? _process.ForOodleLibrary(oodle.Path) : _process;
        if (!ReferenceEquals(worker, _process))
        {
            await MoveToAsync(worker, cancellationToken).ConfigureAwait(false);

            var (response, payload) = await ReadOnceAsync(entryPath, cancellationToken).ConfigureAwait(false);
            if (response.Success) return payload;
            error = response.Error;
        }

        throw new InvalidOperationException(OodleLibrary.Explain(error, oodle) ?? error ?? $"Failed to read '{entryPath}'.");
    }

    /// <summary>Closes this reader's session where it is and opens the pak on <paramref name="worker"/>; no other session on either worker is touched.</summary>
    private async Task MoveToAsync(PakWorkerProcess worker, CancellationToken cancellationToken)
    {
        await CloseSessionAsync(cancellationToken).ConfigureAwait(false);
        _process = worker;
        _client = new PakWorkerClient(worker);
        await OpenLockedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Best effort: a worker that is gone, or a session that died with it, has nothing left to close.</summary>
    private async Task CloseSessionAsync(CancellationToken cancellationToken = default)
    {
        if (!_opened || _sessionLost || _process.IsDead) return;

        try
        {
            var request = new PakWorkerRequest { Opcode = PakWorkerOpcode.CloseReader, SessionId = _sessionId };
            await _client.SendAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort - a worker that's already gone has nothing left to close.
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            CloseSessionAsync().GetAwaiter().GetResult();
            _opened = false; // a second Dispose, or a read after it, has no session to touch
        }
        finally
        {
            _gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reopening '{Pak}' after a worker crash failed; the next read will try again")]
    private static partial void LogReopenFailed(ILogger logger, string pak, Exception exception);
}
