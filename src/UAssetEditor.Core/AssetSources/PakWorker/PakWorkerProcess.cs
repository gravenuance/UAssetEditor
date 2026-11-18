using System.Diagnostics;
using System.IO.Pipes;

namespace UAssetEditor.Core.AssetSources.PakWorker;

/// <summary>
/// Owns the lifetime of the out-of-process pak worker: locates its executable, spawns it
/// with a fresh named pipe, and hands back a connected <see cref="NamedPipeServerStream"/>
/// for <see cref="PakWorkerClient"/> to talk over. One shared instance is reused for the
/// app's whole session (see callers) - a dead worker (process exited, pipe broken) is
/// detected by the caller and a fresh one is spawned transparently via <see cref="Ensure"/>,
/// never eagerly.
/// </summary>
public sealed class PakWorkerProcess : IDisposable
{
    private const string EmbeddedResourceName = "UAssetEditor.PakWorker.exe";
    private const string EnvVarOverride = "UASSETEDITOR_PAKWORKER_PATH";

    /// <summary>One worker reused for the app's (or test process's) whole session - the app only ever has one pak open/being built at a time, so there's nothing to gain from more than one worker, and every direct repak touchpoint (PakAssetSource, PakMountPointReader, PakPacker, PakRepacker) shares this same instance.</summary>
    public static PakWorkerProcess Shared { get; } = new();

    private readonly string? _workerExecutable;
    private Process? _process;
    private NamedPipeServerStream? _pipe;
    private string? _requestedOodlePath;

    public PakWorkerProcess()
    {
    }

    /// <summary>A worker run from <paramref name="workerExecutable"/> instead of the embedded or dev-build one (a test seam: it lets a test pick a clean folder).</summary>
    internal PakWorkerProcess(string workerExecutable) => _workerExecutable = workerExecutable;

    /// <summary>Counts the workers this instance has started; a reader opened on an earlier one has lost its session.</summary>
    public int Generation { get; private set; }

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

    /// <summary>True once this instance's worker process has exited or its pipe has broken - the next <see cref="Ensure"/> call spawns a fresh one.</summary>
    public bool IsDead { get; private set; }

    /// <summary>The connected pipe to the live worker, spawning one first if none is currently alive.</summary>
    public async Task<NamedPipeServerStream> EnsureAsync(CancellationToken cancellationToken = default)
    {
        if (_pipe != null && !IsDead) return _pipe;

        await _ensureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pipe != null && !IsDead) return _pipe; // a concurrent caller may have already respawned it while this one waited

            Cleanup();
            IsDead = false;

            var exePath = ResolveWorkerExecutable();
            var pipeName = $"UAssetEditor.PakWorker.{Environment.ProcessId}.{Guid.NewGuid():N}";

            _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            _process = new Process
            {
                StartInfo = new ProcessStartInfo(exePath, pipeName)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                },
                EnableRaisingEvents = true,
            };
            // The one Oodle path a worker may inherit: a validated absolute file, else none (never a rejected or relative value).
            var oodlePath = _requestedOodlePath ?? OodleLibrary.Locate(null).Path;
            _process.StartInfo.Environment.Remove(OodleLibrary.EnvironmentVariable);
            if (oodlePath != null)
                _process.StartInfo.Environment[OodleLibrary.EnvironmentVariable] = oodlePath;
            OodleLibraryPath = oodlePath;
            Generation++;

            var spawned = _process;
            spawned.Exited += (_, _) =>
            {
                // A worker replaced on purpose exits later than its successor starts.
                if (ReferenceEquals(_process, spawned)) IsDead = true;
            };
            _process.Start();

            try
            {
                await _pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                IsDead = true;
                throw;
            }

            return _pipe;
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    /// <summary>
    /// Makes the next worker start with <paramref name="path"/> as its Oodle DLL, after a read failed for want of one.
    /// repak_bind keeps its first Oodle lookup, a failed one included, for the process's life, so a worker that has
    /// failed cannot be given another DLL: it is replaced. Returns false when the worker the read ran on already holds
    /// that DLL (it genuinely cannot load it), true when a retry on a replacement worker is worth making.
    /// </summary>
    /// <param name="path">A validated absolute DLL path from <see cref="OodleLibrary"/>.</param>
    /// <param name="failedGeneration"><see cref="Generation"/> of the worker the read ran on.</param>
    public async Task<bool> UseOodleLibraryAsync(string path, int failedGeneration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await _ensureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Generation != failedGeneration) return true; // another caller replaced the worker since the read failed
            if (string.Equals(OodleLibraryPath, path, StringComparison.OrdinalIgnoreCase)) return false;

            _requestedOodlePath = path;
            IsDead = true;
            return true;
        }
        finally
        {
            _ensureLock.Release();
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
    }

    public void Dispose()
    {
        Cleanup();
        _ensureLock.Dispose();
    }
}
