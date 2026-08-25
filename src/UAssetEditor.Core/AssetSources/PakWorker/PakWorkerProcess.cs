using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;

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

    private Process? _process;
    private NamedPipeServerStream? _pipe;

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
            _process.Exited += (_, _) => IsDead = true;
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

    /// <summary>Marks the current worker/pipe as dead so the next <see cref="EnsureAsync"/> call spawns a fresh one - called by <see cref="PakWorkerClient"/> when a pipe I/O call fails or the process is observed to have exited.</summary>
    public void MarkDead() => IsDead = true;

    /// <summary>The exit code of the most recently spawned worker process, if it has exited - used to tell a known repak crash (0xC0000409) apart from any other exit reason in status/log text.</summary>
    public int? LastExitCode => _process is { HasExited: true } p ? p.ExitCode : null;

    private static string ResolveWorkerExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable(EnvVarOverride);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        var embedded = TryExtractEmbeddedWorker();
        if (embedded != null) return embedded;

        var devFallback = TryFindDevBuildOutput();
        if (devFallback != null) return devFallback;

        throw new FileNotFoundException(
            $"Could not locate {EmbeddedResourceName} via {EnvVarOverride}, an embedded resource, or a dev build output folder.");
    }

    /// <summary>
    /// Looks for the worker exe embedded as a resource in the process's entry assembly (only
    /// present in a real single-file publish of UAssetEditor.App - see its
    /// PublishAndEmbedPakWorker target). Extracts it once per content hash into LocalAppData
    /// and reuses that copy on subsequent runs/launches, so a normal launch after the first
    /// never pays the extraction cost again. The hash-qualified folder (rather than an app
    /// version number) means an updated embedded worker is picked up automatically and never
    /// collides with a same-version copy that's still locked by a running previous instance.
    /// </summary>
    private static string? TryExtractEmbeddedWorker()
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        using var resourceStream = entryAssembly?.GetManifestResourceStream(EmbeddedResourceName);
        if (resourceStream == null) return null;

        using var buffered = new MemoryStream();
        resourceStream.CopyTo(buffered);
        var bytes = buffered.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16];

        var targetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UAssetEditor", "PakWorker", hash);
        var targetPath = Path.Combine(targetDir, EmbeddedResourceName);

        if (File.Exists(targetPath)) return targetPath;

        Directory.CreateDirectory(targetDir);
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, targetPath, overwrite: true);
        return targetPath;
    }

    /// <summary>
    /// Dev/test fallback: looks for the worker's own build output in a sibling folder,
    /// relative to whatever assembly is currently executing (works for both
    /// UAssetEditor.App run from bin\Debug and UAssetEditor.Core.Tests run via `dotnet
    /// test`, since both sit under a `src\` or `tests\` sibling of `src\UAssetEditor.PakWorker\`
    /// in this repo's known layout). Walks up looking for a directory named "src", then
    /// descends into UAssetEditor.PakWorker's own bin output for the current configuration.
    /// </summary>
    private static string? TryFindDevBuildOutput()
    {
        var searchRoot = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(searchRoot);

        while (dir != null)
        {
            var candidateSrc = dir.Parent?.EnumerateDirectories("src", SearchOption.TopDirectoryOnly).FirstOrDefault()
                ?? (dir.Name == "src" ? dir : null);
            if (candidateSrc != null)
            {
                var workerBin = Path.Combine(candidateSrc.FullName, "UAssetEditor.PakWorker", "bin");
                if (Directory.Exists(workerBin))
                {
                    var exe = Directory.EnumerateFiles(workerBin, EmbeddedResourceName, SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (exe != null) return exe;
                }
            }

            dir = dir.Parent;
        }

        return null;
    }

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
