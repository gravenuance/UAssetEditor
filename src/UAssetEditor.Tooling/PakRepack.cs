using UAssetAPI;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Editing;

namespace UAssetEditor.Tooling;

/// <summary>What a repack wrote: every entry of the source pak, and the edits a rule set made on the way.</summary>
internal sealed record RepackOutcome(int Entries, int AssetsEdited, int Changes);

/// <summary>An asset a repack's rule set could not edit (<see cref="Failed"/>), or could not open and packed unchanged.</summary>
internal sealed record RepackNote(bool Failed, string AssetPath, string? Error)
{
    /// <summary>The note without its tag, which each front end writes its own way.</summary>
    public string Detail => Failed ? $"{AssetPath}: {Error}" : $"{AssetPath}: could not open, packed unchanged: {Error}";
}

/// <summary>Repacks a pak into a new one, optionally applying a rule set to its assets first, for every front end.</summary>
internal static class PakRepack
{
    /// <summary>
    /// Opens <paramref name="pakPath"/> for a repack with nothing extracted up front, whatever the pak's size: a rule set extracts each
    /// asset as it opens it, and <see cref="PakRepacker"/> reads every entry nobody extracted from the pak itself.
    /// </summary>
    public static PakAssetSource OpenSource(string pakPath, byte[]? aesKey, PakVariant variant, string? tempRoot = null) =>
        new(pakPath, aesKey, largePakThresholdBytes: 0, tempRoot, variant);

    /// <summary>
    /// Applies <paramref name="rules"/> (when given) to <paramref name="source"/>'s assets, then writes every entry to <paramref name="output"/>.
    /// An asset the rules could not edit stops the repack before anything is written, since repacking anyway would ship it
    /// unedited without anyone noticing; each such asset, and each one that could not be opened and goes in unchanged, is
    /// reported through <paramref name="log"/>. The caller has checked <paramref name="output"/> (<see cref="OutputPaths"/>).
    /// </summary>
    public static async Task<RepackOutcome> RunAsync(
        PakAssetSource source,
        string output,
        RuleSet? rules,
        ArgReader openSettings,
        Action<RepackNote> log,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(log);
        var edited = 0;
        var changes = 0;

        if (rules != null)
        {
            var versions = await Task.Run(() => AssetIo.ResolveVersions(openSettings), cancellationToken).ConfigureAwait(false);
            var editProgress = progress == null ? null : new EditProgressAdapter(progress);
            var sets = await EditExecutor.ApplyAsync(
                source, versions, rules, createBackup: false, backupFolder: null, editProgress, cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var skipped in sets.Where(s => s.Skipped)) log(new RepackNote(Failed: false, skipped.AssetPath, skipped.Error));
            var failed = sets.Where(s => s.Failed).Select(s => new RepackNote(Failed: true, s.AssetPath, s.Error)).ToList();
            foreach (var note in failed) log(note);
            if (failed.Count > 0)
                throw new ArgException($"{failed.Count} asset(s) failed to edit, so nothing was repacked. First: {failed[0].Detail}");

            edited = sets.Count(s => s.Changed && s.Changes.Count > 0);
            changes = sets.Sum(s => s.Changes.Count);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        // PakRepacker takes no token: once entries are being written, the pak is finished rather than left half-written.
        var result = await Task.Run(() => PakRepacker.Build(source, output), CancellationToken.None).ConfigureAwait(false);
        if (result.HasFailures) throw new ArgException($"Repacking failed: {result.FailedEntries[0].Reason}");
        return new RepackOutcome(result.SucceededCount, edited, changes);
    }

    // Reports on the calling thread, unlike Progress<T>, which posts to a captured context and can arrive after the call returned.
    private sealed class EditProgressAdapter(IProgress<(int Done, int Total)> target) : IProgress<EditProgress>
    {
        public void Report(EditProgress value) => target.Report((value.Completed, value.Total));
    }
}
