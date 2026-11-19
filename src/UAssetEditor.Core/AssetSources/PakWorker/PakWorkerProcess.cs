using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;

namespace UAssetEditor.Core.AssetSources.PakWorker;

/// <summary>
/// Owns the lifetime of one out-of-process pak worker: locates its executable, spawns it
/// with a fresh named pipe, and hands back a connected <see cref="NamedPipeServerStream"/>
/// for <see cref="PakWorkerClient"/> to talk over. A dead worker (process exited, pipe broken) is
/// detected by the caller and a fresh one is spawned transparently via <see cref="EnsureAsync"/>,
/// never eagerly.
/// <para>
/// A worker's Oodle DLL is fixed when it starts, because repak_bind keeps the DLL it first loaded for the
/// process's life. <see cref="Shared"/> is the default worker: it starts with the validated
/// <c>REPAK_OODLE_DLL</c> or none. A DLL found next to a pak gets a worker of its own,
/// <see cref="ForOodleLibrary"/>, so no worker is ever replaced while someone else's session lives in it.
/// </para>
/// </summary>
public sealed class PakWorkerProcess : IDisposable
{
    /// <summary>When set to 1 in the worker's environment, it answers the test-only entries (<c>__TEST_CRASH__</c>, <c>__TEST_STATE__</c>); production workers never do.</summary>
    public const string TestHooksEnvironmentVariable = "UASSETEDITOR_PAKWORKER_TEST_HOOKS";

    /// <summary>How long a started worker has to connect (its own connect gives up after 10 s; the first start also unpacks the embedded exe).</summary>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private const string EmbeddedResourceName = "UAssetEditor.PakWorker.exe";
    private const string EnvVarOverride = "UASSETEDITOR_PAKWORKER_PATH";

    /// <summary>The default worker, reused for the app's (or test process's) whole session; every direct repak touchpoint (PakAssetSource, PakMountPointReader, PakPacker, PakRepacker) shares it unless it needs a DLL of its own.</summary>
    public static PakWorkerProcess Shared { get; } = new();

    private readonly string? _workerExecutable;
    private readonly string? _fixedOodlePath;
    private readonly PakWorkerProcess _root;
    private readonly ConcurrentDictionary<string, PakWorkerProcess> _byOodlePath = new(StringComparer.OrdinalIgnoreCase);
    private Process? _process;
    private NamedPipeServerStream? _pipe;

    public PakWorkerProcess()
    {
        _root = this;
    }

    /// <summary>A worker run from <paramref name="workerExecutable"/> instead of the embedded or dev-build one (a test seam: it lets a test pick a clean folder).</summary>
    internal PakWorkerProcess(string workerExecutable)
    {
        _root = this;
        _workerExecutable = workerExecutable;
    }

    private PakWorkerProcess(PakWorkerProcess root, string oodlePath)
    {
        _root = root;
        _workerExecutable = root._workerExecutable;
        _fixedOodlePath = oodlePath;
    }

    /// <summary>The Oodle DLL the current (or last) worker was started with; null when it was started with none.</summary>
    public string? OodleLibraryPath { get; private set; }

    /// <summary>
    /// Serializes <see cref="EnsureAsync"/> against itself - <see cref="Shared"/> is one
    /// static instance whose <see cref="_pipe"/>/<see cref="_process"/>/<see cref="IsDead"/>
    /// two concurrent callers both observing a dead/absent pipe could otherwise both spawn
    /// a worker and stomp on each other's Process/pipe fields. Current callers happen to
    /// already serialize themselves (the UI only ever runs one busy operation at a time, and
    /// each <see cref="PakAssetSource"/> serializes its own reads via its own lock), but
    /// nothing here should rely on that staying true.
    /// </summary>
    private readonly SemaphoreSlim _ensureLock = new(1, 1);

    /// <summary>
    /// One request at a time on the pipe: a request's frames and its response must not interleave with another caller's
    /// (the MCP server runs tool calls concurrently), or one caller would read the other's response. Held by
    /// <see cref="PakWorkerClient"/> across <see cref="EnsureAsync"/>, the write and the read.
    /// </summary>
    private readonly SemaphoreSlim _requestLock = new(1, 1);

    private volatile bool _disposed;

    /// <summary>Waits for the pipe; dispose the result to let the next request through.</summary>
    internal async Task<IDisposable> AcquireRequestAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new RequestTurn(() => _requestLock.Release());
    }

    private sealed class RequestTurn(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose()
        {
            try
            {
                Interlocked.Exchange(ref _release, null)?.Invoke();
            }
            catch (ObjectDisposedException)
            {
                // The worker was disposed while this request held the turn: nobody is left to wake.
            }
        }
    }

    /// <summary>True once this instance's worker process has exited or its pipe has broken - the next <see cref="EnsureAsync"/> call spawns a fresh one.</summary>
    public bool IsDead { get; private set; }

    /// <summary>The worker that holds <paramref name="oodlePath"/> as its Oodle DLL: this one when it was started with that path, else a worker of its own for it (started on first use, kept for the session).</summary>
    /// <param name="oodlePath">A validated absolute DLL path from <see cref="OodleLibrary"/>.</param>
    public PakWorkerProcess ForOodleLibrary(string oodlePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oodlePath);
        ObjectDisposedException.ThrowIf(_root._disposed, this);

        if (string.Equals(OodleLibraryPath, oodlePath, StringComparison.OrdinalIgnoreCase)) return this;
        if (string.Equals(_root.OodleLibraryPath, oodlePath, StringComparison.OrdinalIgnoreCase)) return _root;

        var worker = _root._byOodlePath.GetOrAdd(oodlePath, static (path, root) => new PakWorkerProcess(root, path), _root);
        if (_root._disposed)
        {
            // Disposed between the check and the add: the root's Dispose may have missed this worker, so stop it here.
            worker.Dispose();
            throw new ObjectDisposedException(nameof(PakWorkerProcess));
        }

        return worker;
    }

    /// <summary>The connected pipe to the live worker, spawning one first if none is currently alive.</summary>
    public async Task<NamedPipeServerStream> EnsureAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipe != null && !IsDead) return _pipe;

        await _ensureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this); // disposed while this caller waited: never start a worker nobody will stop
            if (_pipe != null && !IsDead) return _pipe; // a concurrent caller may have already respawned it while this one waited

            Cleanup(); // leaves IsDead set: it stays so unless a worker starts and connects

            var exePath = ResolveWorkerExecutable();
            var pipeName = $"UAssetEditor.PakWorker.{Environment.ProcessId}.{Guid.NewGuid():N}";
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            // The one Oodle path a worker may inherit: a validated absolute file, else none (never a rejected or relative value).
            var oodlePath = _fixedOodlePath ?? OodleLibrary.Locate(null).Path;
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(exePath, pipeName)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // Never the host's folder, which is wherever the host happened to be started from. The pak handles send absolute paths for this reason.
                    WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "",
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                },
                EnableRaisingEvents = true,
            };
            process.StartInfo.Environment.Remove(OodleLibrary.EnvironmentVariable);
            if (oodlePath != null)
                process.StartInfo.Environment[OodleLibrary.EnvironmentVariable] = oodlePath;
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, _) =>
            {
                exited.TrySetResult();
                if (ReferenceEquals(_process, process)) IsDead = true; // an exit before _process is assigned is caught by the HasExited check below
            };

            try
            {
                process.Start();
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                process.Dispose();
                throw;
            }

            _pipe = pipe;
            _process = process;
            OodleLibraryPath = oodlePath;

            try
            {
                await ConnectAsync(pipe, exited.Task, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Cleanup(); // a worker that never connected is of no use to the next caller either
                throw;
            }

            IsDead = process.HasExited;
            return pipe;
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    /// <summary>
    /// Waits for a just-started worker to connect, but not forever: a worker that exits first (a missing runtime, a crash on
    /// start) or stays silent past <see cref="ConnectTimeout"/> fails the call with an <see cref="IOException"/>, which
    /// <see cref="PakWorkerClient"/> reports as a crashed worker. The caller's own cancellation still surfaces as such.
    /// </summary>
    private static async Task ConnectAsync(NamedPipeServerStream pipe, Task exited, CancellationToken cancellationToken)
    {
        using var give = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        give.CancelAfter(ConnectTimeout);
        var connect = pipe.WaitForConnectionAsync(give.Token);
        try
        {
            if (await Task.WhenAny(connect, exited).ConfigureAwait(false) == connect)
            {
                await connect.ConfigureAwait(false);
                return;
            }

            await give.CancelAsync().ConfigureAwait(false);
            await connect.ConfigureAwait(false); // a connection that won the race after all is fine; a cancelled wait throws below
            return;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && exited.IsCompleted)
        {
            throw new IOException("The pak worker exited before it connected.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The pak worker did not connect in time.");
        }
    }

    /// <summary>Marks the current worker/pipe as dead so the next <see cref="EnsureAsync"/> call spawns a fresh one - called by <see cref="PakWorkerClient"/> when a pipe I/O call fails or the process is observed to have exited.</summary>
    public void MarkDead() => IsDead = true;

    /// <summary>The exit code of the most recently spawned worker process, if it has exited - used to tell a known repak crash (0xC0000409) apart from any other exit reason in status/log text.</summary>
    public int? LastExitCode => _process is { HasExited: true } p ? p.ExitCode : null;

    private string ResolveWorkerExecutable() => _workerExecutable ?? ResolveDefaultWorkerExecutable();

    internal static string ResolveDefaultWorkerExecutable() =>
        EmbeddedToolLocator.Resolve(EmbeddedResourceName, EnvVarOverride, TryFindDevBuildOutput);

    /// <summary>
    /// Dev/test fallback: looks for the worker's own build output in a sibling folder,
    /// relative to whatever assembly is currently executing (works for both
    /// UAssetEditor.App run from bin\Debug and UAssetEditor.Core.Tests run via `dotnet
    /// test`, since both sit under a `src\` or `tests\` sibling of `src\UAssetEditor.PakWorker\`
    /// in this repo's known layout) - descends into UAssetEditor.PakWorker's own bin output
    /// for the current configuration and picks the most recently built exe there.
    /// </summary>
    private static string? TryFindDevBuildOutput() => EmbeddedToolLocator.FindUnderSrcSibling(srcDir =>
    {
        var workerBin = Path.Combine(srcDir.FullName, "UAssetEditor.PakWorker", "bin");
        if (!Directory.Exists(workerBin)) return null;

        return Directory.EnumerateFiles(workerBin, EmbeddedResourceName, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    });

    private void Cleanup()
    {
        _pipe?.Dispose();
        _pipe = null;

        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        }
        _process?.Dispose();
        _process = null;
        IsDead = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var worker in _byOodlePath.Values)
            worker.Dispose();
        Cleanup();
        _ensureLock.Dispose();
        _requestLock.Dispose();
    }
}
