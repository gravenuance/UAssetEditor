namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Extracts every entry of a <see cref="PakAssetSource"/> to a chosen folder on disk,
/// mirroring the pak's own internal paths - the general-purpose counterpart to
/// <see cref="PakAssetSource.ExtractEntry"/>, which only ever writes into a private temp
/// directory for the app's own use while an asset is open. Reads straight from the pak
/// (not from any already-extracted temp copy), so it always reflects the archive on disk,
/// not whatever happens to be open/edited in the current session.
/// </summary>
public static class PakUnpacker
{
    public static void Unpack(
        PakAssetSource source,
        string destinationFolder,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var entries = source.ListAllEntries().ToList();

        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = entries[i];
            var outputPath = Path.Combine(destinationFolder, entry.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, source.ReadOriginalBytes(entry));

            progress?.Report((i + 1, entries.Count));
        }
    }
}
