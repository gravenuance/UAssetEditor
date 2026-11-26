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

var readerSessions = new Dictionary<long, (FileStream Stream, PakReader Reader)>();
var writerSessions = new Dictionary<long, (FileStream Stream, PakWriter Writer)>();

// Session ids start at a random point so that an id from another worker (or from this one before a crash) is never
// valid here: a stale handle gets "unknown session" instead of silently addressing someone else's session.
long nextSessionId = (BitConverter.ToInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)) & ((1L << 62) - 1)) + 1;

// The test-only entries below answer only a worker the tests started with this variable; read once, at start.
var testHooks = Environment.GetEnvironmentVariable(PakWorkerProcess.TestHooksEnvironmentVariable) == "1";

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
                    // The error reply carries no session id, so whatever a failed open created is released here, and the session is only stored once it is complete.
                    var stream = File.Open(request.PakPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
                    PakReader reader;
                    try
                    {
                        using var builder = new PakBuilder();
                        if (!string.IsNullOrEmpty(request.AesKeyHex))
                            builder.Key(Convert.FromHexString(request.AesKeyHex));
                        if (request.Variant is { } variant)
                            builder.Variant(variant);
                        reader = builder.Reader(stream);
                    }
                    catch
                    {
                        await stream.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }

                    string mountPoint;
                    PakVersion version;
                    List<string> entries;
                    try
                    {
                        mountPoint = reader.GetMountPoint();
                        version = reader.GetVersion();
                        entries = reader.Files().ToList();
                    }
                    catch
                    {
                        reader.Dispose();
                        await stream.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }

                    var sessionId = nextSessionId++;
                    readerSessions[sessionId] = (stream, reader);
                    response = new PakWorkerResponse { Success = true, SessionId = sessionId, MountPoint = mountPoint, Version = version, Entries = entries };
                    break;
                }

                case PakWorkerOpcode.ReadEntry:
                {
                    // Test-only fault injection: simulates the confirmed real-world native
                    // crash (abrupt process termination, no response) without needing the
                    // actual buggy repak code path or a real crash-triggering pak entry - see
                    // PakWorkerCrashRecoveryTests. Never matches a real pak entry path.
                    if (testHooks && request.EntryPath == "__TEST_CRASH__")
                        Environment.FailFast("Simulated pak worker crash for testing.");

                    // Test-only: answers nothing until the host closes the pipe (the host sends nothing meanwhile),
                    // standing in for a long read that disposal cuts short (PakWorkerConcurrencyTests); never
                    // matches a real pak entry path. A host that dies still ends this worker.
                    if (testHooks && request.EntryPath == "__TEST_HANG__")
                    {
                        _ = await pipe.ReadAsync(new byte[1]).ConfigureAwait(false);
                        return 0;
                    }

                    // Test-only: reports which process this is and the Oodle path it was started with
                    // (PakWorkerOodleTests); never matches a real pak entry path.
                    if (testHooks && request.EntryPath == "__TEST_STATE__")
                    {
                        responsePayload = System.Text.Encoding.UTF8.GetBytes(
                            $"{Environment.ProcessId}|{Environment.GetEnvironmentVariable(OodleLibrary.EnvironmentVariable)}");
                        response = new PakWorkerResponse { Success = true };
                        break;
                    }

                    if (!readerSessions.TryGetValue(request.SessionId, out var session))
                    {
                        response = new PakWorkerResponse { Success = false, UnknownSession = true, Error = $"Unknown reader session {request.SessionId}." };
                        break;
                    }

                    // A failed read (missing entry, decompression failure, anything) throws with repak's own message,
                    // which the catch below sends back as the response's error.
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
                    PakWriter writer;
                    try
                    {
                        using var builder = new PakBuilder();
                        if (!string.IsNullOrEmpty(request.AesKeyHex))
                            builder.Key(Convert.FromHexString(request.AesKeyHex));
                        if (request.Compression != null)
                            builder.Compression(request.Compression);
                        if (request.Variant is { } variant)
                            builder.Variant(variant);
                        writer = builder.Writer(stream, request.Version ?? PakVersion.V11, request.MountPoint ?? "", pathHashSeed: 0);
                    }
                    catch
                    {
                        await stream.DisposeAsync().ConfigureAwait(false); // the error reply carries no session id, so nobody could close it
                        throw;
                    }

                    var sessionId = nextSessionId++;
                    writerSessions[sessionId] = (stream, writer);
                    response = new PakWorkerResponse { Success = true, SessionId = sessionId };
                    break;
                }

                case PakWorkerOpcode.WriteFile:
                {
                    if (!writerSessions.TryGetValue(request.SessionId, out var wsession))
                    {
                        response = new PakWorkerResponse { Success = false, UnknownSession = true, Error = $"Unknown writer session {request.SessionId}." };
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
                        response = new PakWorkerResponse { Success = false, UnknownSession = true, Error = $"Unknown writer session {request.SessionId}." };
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
            response = new PakWorkerResponse { Success = false, Error = PakWorkerError.Describe(ex), SessionId = request.SessionId };
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
