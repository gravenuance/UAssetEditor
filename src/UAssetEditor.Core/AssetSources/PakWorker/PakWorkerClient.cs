namespace UAssetEditor.Core.AssetSources.PakWorker;

/// <summary>
/// Sends one request and awaits its response over a <see cref="PakWorkerProcess"/>'s pipe,
/// translating any I/O failure (broken pipe, unexpected EOF, malformed frame, a worker that never connects) into a
/// <see cref="PakWorkerCrashedException"/> and marking the process dead so the next call
/// spawns a fresh one. The pipe carries one request at a time: callers run concurrently (the MCP server) and a
/// second caller's frames in the middle would hand one caller the other's response, so the exchange (write, read)
/// holds the process's request turn. Starting the worker is bounded (see <see cref="PakWorkerProcess.EnsureAsync"/>)
/// and a caller cancelled before it wrote anything leaves the worker alone; one cancelled after that leaves the pipe
/// mid-frame, so that worker is marked dead rather than reused. A request that the worker's disposal cuts short throws
/// <see cref="ObjectDisposedException"/>, not a crash.
/// </summary>
public sealed class PakWorkerClient(PakWorkerProcess process)
{
    public Task<(PakWorkerResponse Response, byte[] Payload)> SendAsync(
        PakWorkerRequest request, ReadOnlyMemory<byte> payload = default, string? operation = null, CancellationToken cancellationToken = default) =>
        ExchangeAsync(request, payload, operation, PakWorkerFraming.ReadMessageAsync<PakWorkerResponse>, cancellationToken);

    /// <summary>Same as <see cref="SendAsync"/>, but the response payload comes back as a pooled <see cref="RentedBuffer"/> - see <see cref="PakWorkerFraming.ReadPooledMessageAsync{THeader}"/>. Used for <see cref="PakWorkerOpcode.ReadEntry"/>, where the payload is pak entry bytes that can be large and are read once per entry.</summary>
    public Task<(PakWorkerResponse Response, RentedBuffer Payload)> SendPooledAsync(
        PakWorkerRequest request, string? operation = null, CancellationToken cancellationToken = default) =>
        ExchangeAsync(request, ReadOnlyMemory<byte>.Empty, operation, PakWorkerFraming.ReadPooledMessageAsync<PakWorkerResponse>, cancellationToken);

    /// <summary>How long <see cref="FinishSoon"/> holds its caller.</summary>
    internal static readonly TimeSpan DisposeGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Runs a best-effort clean-up (closing a session in the worker) off the calling thread and waits at most
    /// <see cref="DisposeGrace"/> for it: an idle worker answers at once, so the pak file is released when
    /// <c>Dispose</c> returns, while a worker busy with someone else's long request cannot freeze the caller (the UI thread);
    /// the clean-up then finishes in the background. Failures are ignored: a worker that is gone has nothing left to close.
    /// </summary>
    internal static void FinishSoon(Func<Task> cleanup)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await cleanup().ConfigureAwait(false);
            }
            catch
            {
                // Best effort - a worker that's already gone has nothing left to close.
            }
        });
        task.Wait(DisposeGrace);
    }

    /// <summary>The one request/response exchange both public calls go through.</summary>
    private async Task<TResult> ExchangeAsync<TResult>(
        PakWorkerRequest request, ReadOnlyMemory<byte> payload, string? operation,
        Func<Stream, CancellationToken, Task<TResult>> readResponse, CancellationToken cancellationToken)
    {
        using var turn = await process.AcquireRequestAsync(cancellationToken).ConfigureAwait(false);
        var exchanged = false;
        try
        {
            var pipe = await process.EnsureAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); // still nothing on the wire: the worker stays usable for everyone else
            exchanged = true;
            await PakWorkerFraming.WriteMessageAsync(pipe, request, payload, cancellationToken).ConfigureAwait(false);
            return await readResponse(pipe, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (process.IsDisposed
                                   && (ex is IOException or ObjectDisposedException
                                       || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Disposal (the host shutting down) killed the worker or closed the pipe under this request: not a crash. A
            // malformed frame stays one, and the caller's own cancellation stays cancellation.
            if (ex is ObjectDisposedException) throw;
            throw PakWorkerProcess.Stopped(ex);
        }
        catch (OperationCanceledException) when (exchanged)
        {
            process.MarkDead(); // the pipe holds half a frame, or a response nobody will read
            throw;
        }
        catch (Exception ex) when (IsPipeFailure(ex))
        {
            process.MarkDead();
            var suffix = operation != null ? $" while {operation}" : "";
            throw new PakWorkerCrashedException($"The pak worker process crashed{suffix}.", operation, process.LastExitCode, ex);
        }
    }

    private static bool IsPipeFailure(Exception ex) => ex is IOException or EndOfStreamException or InvalidDataException or ObjectDisposedException;
}
