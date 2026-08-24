namespace UAssetEditor.Core.AssetSources.PakWorker;

/// <summary>
/// Sends one request and awaits its response over a <see cref="PakWorkerProcess"/>'s pipe,
/// translating any I/O failure (broken pipe, unexpected EOF, malformed frame) into a
/// <see cref="PakWorkerCrashedException"/> and marking the process dead so the next call
/// spawns a fresh one. Calls are inherently serialized by the pipe itself - see
/// <see cref="PakWorkerProcess"/>'s remarks on why no request pipelining is needed.
/// </summary>
public sealed class PakWorkerClient(PakWorkerProcess process)
{
    public async Task<(PakWorkerResponse Response, byte[] Payload)> SendAsync(
        PakWorkerRequest request, ReadOnlyMemory<byte> payload = default, string? operation = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var pipe = await process.EnsureAsync(cancellationToken).ConfigureAwait(false);
            await PakWorkerFraming.WriteMessageAsync(pipe, request, payload, cancellationToken).ConfigureAwait(false);
            return await PakWorkerFraming.ReadMessageAsync<PakWorkerResponse>(pipe, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or ObjectDisposedException)
        {
            process.MarkDead();
            var suffix = operation != null ? $" while {operation}" : "";
            throw new PakWorkerCrashedException($"The pak worker process crashed{suffix}.", operation, process.LastExitCode, ex);
        }
    }
}
