using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

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
    /// from. Always passes --no-script-objects: this app has no feature that reads a converted
    /// container's script-objects output, and unconditionally attempting to extract them is
    /// real-world confirmed to hard-fail the *entire* conversion - assets and all - for a small,
    /// standalone container (e.g. a single-mod .utoc) that doesn't carry a ScriptObjects chunk
    /// of its own, which only the game's own full/global container normally does.
    /// </summary>
    public static async Task ConvertToLegacyAsync(
        string utocPath, string output, IReadOnlyList<string> filters, byte[]? aesKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utocPath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(filters);

        var fixedArgs = new List<string> { "to-legacy", utocPath, output, "--no-script-objects" };
        AddAesKey(fixedArgs, aesKey);
        var batches = BatchFilters(filters, string.Join(' ', fixedArgs).Length);

        // retoc truncates its .pak output fresh on every invocation (action_to_legacy's pak
        // branch always opens a new File::create), unlike a loose-folder output (FSFileWriter's
        // own write_file only ever adds/overwrites individual files, never wipes the directory)
        // - so more than one batch here would silently keep only the *last* batch's entries.
        // Refuse outright rather than quietly losing data; a selection this large can still go
        // to a loose folder instead.
        if (batches.Count > 1 && string.Equals(Path.GetExtension(output), ".pak", StringComparison.OrdinalIgnoreCase))
            throw new IoStoreConversionException(
                $"{filters.Count} entries is too large to convert directly to one .pak (would need {batches.Count} retoc calls, each of which truncates the .pak fresh) - convert to a loose folder instead.",
                "Selection too large for one .pak - convert to a loose folder instead.",
                exitCode: 0);

        // retoc reports a per-package conversion failure and keeps going rather than failing
        // the whole process - confirmed against a real user session where 41 of 41 requested
        // assets failed (unresolvable cross-package imports - a single-mod .utoc typically
        // can't resolve imports into the base game's own containers on its own) yet retoc still
        // exited 0. Each per-package failure is logged at info level, but - because a progress
        // bar is active while packages are being processed - through indicatif's own output
        // target (stderr), not the plain stdout the final "Extracted N (M failed)" summary line
        // below uses once the progress bar is gone. Capturing *both*, across every batch, and
        // treating "0 succeeded overall" as a real failure means the log finally shows retoc's
        // real per-package reasons, instead of an empty output folder with nothing to explain
        // why.
        var totalSucceeded = 0;
        var sawSummaryLine = false;
        var diagnostics = new List<string>();

        foreach (var batch in batches)
        {
            var args = new List<string> { "to-legacy", utocPath, output, "--no-script-objects" };
            foreach (var filter in batch)
            {
                args.Add("-f");
                args.Add(filter);
            }
            AddAesKey(args, aesKey);

            var stdOutLines = new List<string>();
            var stdErr = await RunAsync(args, stdOutLines.Add, cancellationToken).ConfigureAwait(false);

            if (TryGetSucceededAssetCount(stdOutLines, out var succeeded))
            {
                sawSummaryLine = true;
                totalSucceeded += succeeded;
            }
            diagnostics.AddRange(stdOutLines);
            diagnostics.AddRange(SplitNonEmptyLines(stdErr));
        }

        if (sawSummaryLine && totalSucceeded == 0)
        {
            var detail = diagnostics.Count > 0 ? $": {string.Join('\n', diagnostics)}" : "";
            throw new IoStoreConversionException(
                $"retoc to-legacy {utocPath} -> {output} converted 0 assets across {batches.Count} batch(es){detail}",
                "No assets converted - see the log for retoc's reason.",
                exitCode: 0);
        }
    }

    // Windows' CreateProcess has a hard ~32,767-character command-line limit - a single retoc
    // call with enough -f filters (a big checked folder/container can mean hundreds of long
    // asset paths) can exceed it, confirmed by a real Win32Exception(206) "The filename or
    // extension is too long" converting a large selection. Splits into as many sequential
    // batches as needed to stay safely under that limit. An empty filter list (convert every
    // entry) is never split - there's nothing to batch, and splitting it would turn "convert
    // everything" into "convert everything, N times".
    private const int MaxCommandLineLength = 30_000;

    private static List<List<string>> BatchFilters(IReadOnlyList<string> filters, int fixedLength)
    {
        if (filters.Count == 0)
            return [[]];

        var batches = new List<List<string>>();
        var batch = new List<string>();
        var length = fixedLength;
        foreach (var filter in filters)
        {
            var entryLength = filter.Length + 4; // "-f " plus the filter text itself, roughly
            if (batch.Count > 0 && length + entryLength > MaxCommandLineLength)
            {
                batches.Add(batch);
                batch = new List<string>();
                length = fixedLength;
            }
            batch.Add(filter);
            length += entryLength;
        }
        batches.Add(batch);
        return batches;
    }

    private static IEnumerable<string> SplitNonEmptyLines(string text) =>
        text.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0);

    // Matches retoc's own to-legacy summary line, e.g. "info: Extracted 3 (1 failed) legacy
    // assets to ...". Absent entirely when --no-assets was passed, in which case there's
    // nothing to check here - the caller didn't ask for assets in the first place.
    private static readonly Regex ExtractedAssetsSummaryRegex = new(@"Extracted (\d+) \(\d+ failed\) legacy assets", RegexOptions.Compiled);

    private static bool TryGetSucceededAssetCount(IReadOnlyList<string> stdOutLines, out int succeeded)
    {
        foreach (var line in stdOutLines)
        {
            var match = ExtractedAssetsSummaryRegex.Match(line);
            if (match.Success)
            {
                succeeded = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                return true;
            }
        }

        succeeded = 0;
        return false;
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

    // --aes-key is a *global* retoc option, recognized only before the subcommand
    // (`retoc.exe --aes-key <KEY> list ...`) even though every subcommand's own --help is
    // silent about it - confirmed against the real vendored retoc.exe, which rejects
    // `list --path <UTOC> --aes-key <KEY>` with "unexpected argument '--aes-key' found".
    // Passed as bare hex, no "0x" prefix, matching typical Rust hex-parsing crate convention.
    private static void AddAesKey(List<string> args, byte[]? aesKey)
    {
        if (aesKey is { Length: > 0 })
        {
            args.Insert(0, Convert.ToHexString(aesKey));
            args.Insert(0, "--aes-key");
        }
    }

    /// <summary>Returns the process's captured stderr text on success (exit code 0) - <see cref="ConvertToLegacyAsync"/> needs it even then, since retoc can report per-package failures there without a nonzero exit. Throws (with stderr already folded into the exception) on any other exit code.</summary>
    private static async Task<string> RunAsync(List<string> args, Action<string> onStdOutLine, CancellationToken cancellationToken)
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

        // Cancellation only stops this method from waiting - it doesn't touch the child process,
        // so a canceled convert/pack would otherwise leave retoc.exe running (and still writing
        // to the output the user was just told got canceled) unless it's killed here too.
        await using var killOnCancel = cancellationToken.Register(static state =>
        {
            var p = (Process)state!;
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        }, process).ConfigureAwait(false);

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
            var message = $"retoc {command} failed (exit code {process.ExitCode}){detail}";
            throw new IoStoreConversionException(message, SummarizeStdErr(stdErr, process.ExitCode), process.ExitCode);
        }

        return stdErr;
    }

    // retoc's stderr on failure is Clap's usual multi-paragraph dump (the real error, then a
    // blank line, then a "tip:" and a full "Usage:"/"--help" block) - far too long for a UI
    // status line, unlike the first line alone, which is consistently retoc's actual complaint
    // (e.g. "error: unexpected argument '--aes-key' found"). The full text still reaches the
    // log file via the exception's own Message.
    private static string SummarizeStdErr(string stdErr, int exitCode)
    {
        var firstLine = stdErr
            .Split('\n')
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => line.Length > 0);

        if (string.IsNullOrEmpty(firstLine))
            return $"retoc exited with code {exitCode}.";

        const int maxLength = 160;
        return firstLine.Length > maxLength ? firstLine[..maxLength] + "..." : firstLine;
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
