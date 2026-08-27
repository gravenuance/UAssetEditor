using System.Diagnostics;

namespace UAssetEditor.Core.AssetSources.IoStore;

/// <summary>
/// Shells out to the vendored `retoc.exe` (see <see cref="EmbeddedToolLocator"/>,
/// THIRD_PARTY_NOTICES.md) to list, and convert between, Unreal Engine's IoStore/Zen container
/// format (.utoc/.ucas) and legacy .pak-compatible format. Unlike
/// <see cref="PakWorker.PakWorkerProcess"/>, retoc is a one-shot CLI - each call spawns and
/// waits for a fresh process; there's no persistent session or crash-respawn story to manage.
/// </summary>
public static class RetocProcess
{
    private const string EmbeddedResourceName = "retoc.exe";
    private const string EnvVarOverride = "UASSETEDITOR_RETOC_PATH";

    /// <summary>Lists every chunk's path in a .utoc container, without converting anything. Chunks with no real per-package path (e.g. the container's own header) are omitted.</summary>
    public static async Task<IReadOnlyList<string>> ListAsync(string utocPath, byte[]? aesKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utocPath);

        // `list --path` alone (verified against the real retoc.exe, not guessed) prints one
        // fixed-width-padded row per chunk: "<name> <id> <type> <path>" - path is always the
        // last whitespace-separated token in that 4-column shape, and "-" means this chunk (a
        // ContainerHeader, script objects, etc.) has no real per-package path to show. Real
        // package paths never contain whitespace, so splitting on whitespace and taking the
        // last token is safe even though the name/type columns are otherwise free-form.
        var args = new List<string> { "list", utocPath, "--path" };
        AddAesKey(args, aesKey);

        var paths = new List<string>();
        await RunAsync(args, line =>
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) return;

            var path = fields[^1];
            if (path == "-") return;

            // UE package paths conventionally start with '/' (e.g. "/Game/Foo/Bar"), but
            // PathTreeBuilder.Build (shared with the pak/loose-folder trees, whose paths never
            // start with '/') splits on '/' with RemoveEmptyEntries and would silently drop a
            // leading one when reconstructing each node's FullPath - which CollectSelectedZenAssetPaths
            // later feeds straight back into ConvertToLegacyAsync's -f filter. Stripping it here,
            // once, at the single source of truth, means the tree and the filter always agree
            // with each other on the same (slash-less) form - if that form's actually still
            // "-f"-matchable is unverified against real cooked content (see RetocProcessTests'
            // own remarks on why), but the two no longer silently disagree with each other.
            paths.Add(path.TrimStart('/'));
        }, cancellationToken).ConfigureAwait(false);

        return paths;
    }

    /// <summary>
    /// Converts the given entries (asset filenames, as retoc's own -f/--filter expects) from
    /// <paramref name="utocPath"/> into legacy format at <paramref name="output"/> - a loose
    /// folder, or (confirmed via `to-legacy --help`: "Output directory or .pak") a .pak file
    /// directly. An empty <paramref name="filters"/> list converts every entry. Engine version
    /// is left to retoc's own auto-detection - confirmed via `to-legacy --help` that --version
    /// is only an override there, unlike to-zen where retoc has nothing of its own to detect it
    /// from.
    /// </summary>
    public static Task ConvertToLegacyAsync(
        string utocPath, string output, IReadOnlyList<string> filters, byte[]? aesKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utocPath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(filters);

        var args = new List<string> { "to-legacy", utocPath, output };
        foreach (var filter in filters)
        {
            args.Add("-f");
            args.Add(filter);
        }
        AddAesKey(args, aesKey);

        return RunAsync(args, static _ => { }, cancellationToken);
    }

    /// <summary>
    /// Converts a legacy-format loose folder, or an existing .pak, back into a fresh
    /// .utoc/.ucas pair at <paramref name="outputUtocPath"/> - retoc's own to-zen accepts
    /// either kind of input directly (confirmed via `to-zen --help`: "Input directory or
    /// .pak"), so no intermediate re-pack is needed when the source is already a .pak.
    /// <paramref name="retocEngineVersion"/> must be one of the strings
    /// <see cref="EngineVersionMapping.ToRetocVersion"/> returns (e.g. "UE5_3") - required by
    /// retoc itself for this direction, unlike to-legacy.
    /// </summary>
    public static Task ConvertToZenAsync(
        string input, string outputUtocPath, string retocEngineVersion, byte[]? aesKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(outputUtocPath);
        ArgumentNullException.ThrowIfNull(retocEngineVersion);

        var args = new List<string> { "to-zen", input, outputUtocPath, "--version", retocEngineVersion };
        AddAesKey(args, aesKey);

        return RunAsync(args, static _ => { }, cancellationToken);
    }

    // Not confirmed against a real encrypted container (retoc's own docs give no example key
    // value) - passed as bare hex, no "0x" prefix, matching typical Rust hex-parsing crate
    // convention. If a real encrypted .utoc round trip fails specifically on key parsing, this
    // is the first thing to revisit.
    private static void AddAesKey(List<string> args, byte[]? aesKey)
    {
        if (aesKey is { Length: > 0 })
        {
            args.Add("--aes-key");
            args.Add(Convert.ToHexString(aesKey));
        }
    }

    private static async Task RunAsync(List<string> args, Action<string> onStdOutLine, CancellationToken cancellationToken)
    {
        var exePath = ResolveExecutable();

        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Drain both streams concurrently with waiting for exit rather than reading one and
        // then the other - a process that fills the other stream's OS pipe buffer while only
        // one side is being read blocks trying to write and never exits, which would hang this
        // await forever instead of just failing.
        var stdOutTask = ReadLinesAsync(process.StandardOutput, onStdOutLine, cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stdOutTask.ConfigureAwait(false);
        var stdErr = await stdErrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var command = string.Join(' ', args);
            var detail = string.IsNullOrWhiteSpace(stdErr) ? "" : $": {stdErr.Trim()}";
            throw new IoStoreConversionException($"retoc {command} failed (exit code {process.ExitCode}){detail}", process.ExitCode);
        }
    }

    // Reads line-by-line rather than ReadToEndAsync().Split('\n') - a container with hundreds
    // of thousands of entries shouldn't need one giant buffered string just to enumerate paths.
    private static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            onLine(line);
    }

    private static string ResolveExecutable() =>
        EmbeddedToolLocator.Resolve(EmbeddedResourceName, EnvVarOverride, TryFindVendoredCopy);

    /// <summary>Dev/test fallback: retoc.exe isn't built from this repo's own source (see THIRD_PARTY_NOTICES.md) - it's vendored directly at src/UAssetEditor.App/vendor/retoc.exe, so unlike PakWorkerProcess's dev fallback there's no bin output to search, just that one known path.</summary>
    private static string? TryFindVendoredCopy() => EmbeddedToolLocator.FindUnderSrcSibling(srcDir =>
    {
        var vendored = Path.Combine(srcDir.FullName, "UAssetEditor.App", "vendor", EmbeddedResourceName);
        return File.Exists(vendored) ? vendored : null;
    });
}
