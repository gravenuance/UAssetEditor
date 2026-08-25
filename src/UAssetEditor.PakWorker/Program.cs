using System.IO.Pipes;
using UAssetAPI;
using UAssetEditor.Core.AssetSources.PakWorker;

// Hosts every call into UAssetAPI's embedded native repak_bind.dll in this separate process -
// see PakWorkerProcess (Core) for why, and PakWorkerProtocol for the wire format. Connects as
// the pipe CLIENT (the main app is the server - see PakWorkerProcess.EnsureAsync) using the
// pipe name passed as the sole command-line argument, then dispatches requests until the pipe
// closes or the process is killed.
if (args.Length == 0)
{
    await Console.Error.WriteLineAsync("UAssetEditor.PakWorker: expected a pipe name argument.").ConfigureAwait(false);
    return 1;
}

using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
await pipe.ConnectAsync(10_000).ConfigureAwait(false);

var readerSessions = new Dictionary<int, (FileStream Stream, PakReader Reader)>();
var writerSessions = new Dictionary<int, (FileStream Stream, PakWriter Writer)>();
var nextSessionId = 1;

try
{
    while (true)
    {
        PakWorkerRequest request;
        byte[] requestPayload;
        try
        {
            (request, requestPayload) = await PakWorkerFraming.ReadMessageAsync<PakWorkerRequest>(pipe).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            break; // Client closed the pipe - nothing more to do.
        }

        var responsePayload = Array.Empty<byte>();
        PakWorkerResponse response;

        try
        {
            switch (request.Opcode)
            {
                case PakWorkerOpcode.OpenReader:
                {
                    var stream = File.Open(request.PakPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var builder = new PakBuilder();
                    if (!string.IsNullOrEmpty(request.AesKeyHex))
                        builder.Key(Convert.FromHexString(request.AesKeyHex));
                    var reader = builder.Reader(stream);

                    var sessionId = nextSessionId++;
                    readerSessions[sessionId] = (stream, reader);

                    response = new PakWorkerResponse
                    {
                        Success = true,
                        SessionId = sessionId,
                        MountPoint = reader.GetMountPoint(),
                        Version = reader.GetVersion(),
                        Entries = reader.Files().ToList(),
                    };
                    break;
                }

                case PakWorkerOpcode.ReadEntry:
                {
                    // Test-only fault injection: simulates the confirmed real-world native
                    // crash (abrupt process termination, no response) without needing the
                    // actual buggy repak code path or a real crash-triggering pak entry - see
                    // PakWorkerCrashRecoveryTests. Never matches a real pak entry path.
                    if (request.EntryPath == "__TEST_CRASH__")
                        Environment.FailFast("Simulated pak worker crash for testing.");

                    if (!readerSessions.TryGetValue(request.SessionId, out var session))
                    {
                        response = new PakWorkerResponse { Success = false, Error = $"Unknown reader session {request.SessionId}." };
                        break;
                    }

                    responsePayload = session.Reader.Get(session.Stream, request.EntryPath!);
                    response = new PakWorkerResponse { Success = true, SessionId = request.SessionId };
                    break;
                }

                case PakWorkerOpcode.CloseReader:
                {
                    if (readerSessions.Remove(request.SessionId, out var session))
                    {
                        session.Reader.Dispose();
                        await session.Stream.DisposeAsync().ConfigureAwait(false);
                    }
                    response = new PakWorkerResponse { Success = true, SessionId = request.SessionId };
                    break;
                }

                case PakWorkerOpcode.OpenWriter:
                {
                    var stream = File.Create(request.PakPath!);
                    using var builder = new PakBuilder();
                    if (!string.IsNullOrEmpty(request.AesKeyHex))
                        builder.Key(Convert.FromHexString(request.AesKeyHex));
                    if (request.Compression != null)
                        builder.Compression(request.Compression);
                    var writer = builder.Writer(stream, request.Version ?? PakVersion.V11, request.MountPoint ?? "", pathHashSeed: 0);

                    var sessionId = nextSessionId++;
                    writerSessions[sessionId] = (stream, writer);
                    response = new PakWorkerResponse { Success = true, SessionId = sessionId };
                    break;
                }

                case PakWorkerOpcode.WriteFile:
                {
                    if (!writerSessions.TryGetValue(request.SessionId, out var wsession))
                    {
                        response = new PakWorkerResponse { Success = false, Error = $"Unknown writer session {request.SessionId}." };
                        break;
                    }

                    wsession.Writer.WriteFile(request.EntryPath!, requestPayload);
                    response = new PakWorkerResponse { Success = true, SessionId = request.SessionId };
                    break;
                }

                case PakWorkerOpcode.WriteIndex:
                {
                    if (!writerSessions.TryGetValue(request.SessionId, out var wsession))
                    {
                        response = new PakWorkerResponse { Success = false, Error = $"Unknown writer session {request.SessionId}." };
                        break;
                    }

                    wsession.Writer.WriteIndex();
                    response = new PakWorkerResponse { Success = true, SessionId = request.SessionId };
                    break;
                }

                case PakWorkerOpcode.CloseWriter:
                {
                    if (writerSessions.Remove(request.SessionId, out var wsession))
                    {
                        wsession.Writer.Dispose();
                        await wsession.Stream.DisposeAsync().ConfigureAwait(false);
                    }
                    response = new PakWorkerResponse { Success = true, SessionId = request.SessionId };
                    break;
                }

                default:
                    response = new PakWorkerResponse { Success = false, Error = $"Unknown opcode '{request.Opcode}'." };
                    break;
            }
        }
        catch (Exception ex)
        {
            response = new PakWorkerResponse { Success = false, Error = ex.Message, SessionId = request.SessionId };
        }

        await PakWorkerFraming.WriteMessageAsync(pipe, response, responsePayload).ConfigureAwait(false);
    }
}
finally
{
    foreach (var (stream, reader) in readerSessions.Values)
    {
        reader.Dispose();
        await stream.DisposeAsync().ConfigureAwait(false);
    }
    foreach (var (stream, writer) in writerSessions.Values)
    {
        writer.Dispose();
        await stream.DisposeAsync().ConfigureAwait(false);
    }
}

return 0;
