namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Extracts entries of a <see cref="PakAssetSource"/> to a chosen folder on disk, mirroring
/// the pak's own internal paths - the general-purpose counterpart to
/// <see cref="PakAssetSource.ExtractEntry"/>, which only ever writes into a private temp
/// directory for the app's own use while an asset is open. Reads straight from the pak
/// (not from any already-extracted temp copy), so it always reflects the archive on disk,
/// not whatever happens to be open/edited in the current session.
///
/// Unlike the write-side pak operations (<see cref="PakPacker"/>/<see cref="PakRepacker"/>),
/// this is read-only and each entry lands as an independent file - a worker crash reading
/// one entry doesn't prevent writing the next, so a failure here is recorded and skipped
/// rather than aborting the whole unpack (see <see cref="PakBulkResult.FailedEntries"/>).
/// </summary>
public static class PakUnpacker
{
    /// <param name="entryFilter">When given, only entries this returns true for are extracted - e.g. one checked folder/asset's subtree, rather than the whole pak.</param>
    public static PakBulkResult Unpack(
        PakAssetSource source,
        string destinationFolder,
        Func<string, bool>? entryFilter = null,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var entries = source.ListAllEntries().Where(e => entryFilter?.Invoke(e) ?? true).ToList();
        var failures = new List<(string Entry, string Reason)>();
        var succeeded = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = entries[i];
            try
            {
                var outputPath = Path.Combine(destinationFolder, entry.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllBytes(outputPath, source.ReadOriginalBytes(entry));
                succeeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add((entry, ex.Message));
            }

            progress?.Report((i + 1, entries.Count));
        }

        return new PakBulkResult { SucceededCount = succeeded, FailedEntries = failures };
    }
}
