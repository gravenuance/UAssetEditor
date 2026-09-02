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
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinationFolder);

        // Pak entry paths are untrusted external input - a crafted pak could name an entry
        // "../../../../Windows/System32/evil.dll" to write outside destinationFolder entirely.
        // Resolving destinationFolder once, up front, to its full form is what makes the
        // per-entry containment check below a plain string-prefix comparison.
        var destinationRoot = Path.GetFullPath(destinationFolder);
        var destinationRootWithSeparator = destinationRoot.EndsWith(Path.DirectorySeparatorChar)
            ? destinationRoot
            : destinationRoot + Path.DirectorySeparatorChar;

        var entries = source.ListAllEntries().Where(e => entryFilter?.Invoke(e) ?? true).ToList();
        var failures = new List<(string Entry, string Reason)>();
        var succeeded = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = entries[i];
            try
            {
                var outputPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.Replace('/', Path.DirectorySeparatorChar)));
                if (!outputPath.StartsWith(destinationRootWithSeparator, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Entry path '{entry}' escapes the destination folder.");

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                using (var rented = source.ReadOriginalBytes(entry))
                    File.WriteAllBytes(outputPath, rented.Span);
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
