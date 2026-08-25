using UAssetAPI;
using UAssetEditor.Core.AssetSources.PakWorker;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Builds a new .pak from a <see cref="PakAssetSource"/>: by default every entry from the
/// original archive is included (or only a chosen subset - see the entryFilter parameter),
/// edited entries come from their temp working copy and everything else is streamed through
/// unchanged straight from the source pak. This makes repacking correct regardless of how
/// much of the archive was ever opened - a large, lazily extracted pak that only had a
/// handful of entries touched still repacks completely, one entry at a time, without needing
/// the whole archive resident anywhere at once. Always writes to a new file; never overwrites
/// <see cref="PakAssetSource.PakPath"/>.
///
/// Like <see cref="PakPacker"/>, the actual writing goes through the out-of-process worker -
/// a crash (whether reading an untouched source entry or writing the new pak) fails this
/// whole attempt and discards the partial output, since the new pak's writer session isn't
/// resumable either way; it doesn't take the app down with it.
/// </summary>
public static class PakRepacker
{
    /// <param name="version">
    /// Defaults to <paramref name="source"/>'s own pak version rather than a fixed one -
    /// writing back out at the wrong version is exactly the kind of thing that inspects as
    /// structurally fine (this tool's own reader can still parse it) while the actual game
    /// silently refuses to recognize the file, since its pak-mounting code is version-aware
    /// in ways a generic reader isn't. Only override this if you specifically need to
    /// convert a pak to a different version.
    /// </param>
    /// <param name="entryFilter">When given, only entries this returns true for are included - e.g. a chosen subset of a mod's changes split out into its own pak, still at the source's own version/mount point. Entries this excludes are simply omitted, not written as empty/missing.</param>
    public static PakBulkResult Build(
        PakAssetSource source,
        string outputPakPath,
        PakVersion? version = null,
        PakCompression[]? compression = null,
        byte[]? aesKey = null,
        Func<string, bool>? entryFilter = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var writer = new PakWriterHandle(PakWorkerProcess.Shared);
        try
        {
            writer.OpenAsync(outputPakPath, source.MountPoint, version ?? source.Version, compression, aesKey).GetAwaiter().GetResult();

            var count = 0;
            foreach (var entry in source.ListAllEntries())
            {
                if (entryFilter != null && !entryFilter(entry))
                    continue;

                var bytes = source.TryGetExtractedPath(entry, out var tempPath)
                    ? File.ReadAllBytes(tempPath)
                    : source.ReadOriginalBytes(entry);

                writer.WriteFileAsync(entry, bytes).GetAwaiter().GetResult();
                count++;
            }

            writer.WriteIndexAsync().GetAwaiter().GetResult();
            return new PakBulkResult { SucceededCount = count };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(outputPakPath);
            return new PakBulkResult { FailedEntries = [(outputPakPath, ex.Message)] };
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort - a leftover partial pak isn't worth failing over */ }
    }
}
