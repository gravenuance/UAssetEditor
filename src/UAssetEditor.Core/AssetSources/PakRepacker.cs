using UAssetAPI;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Builds a new .pak from a <see cref="PakAssetSource"/>: every entry from the original
/// archive is included, edited entries come from their temp working copy and everything
/// else is streamed through unchanged straight from the source pak. This makes repacking
/// correct regardless of how much of the archive was ever opened - a large, lazily
/// extracted pak that only had a handful of entries touched still repacks completely,
/// one entry at a time, without needing the whole archive resident anywhere at once.
/// Always writes to a new file; never overwrites <see cref="PakAssetSource.PakPath"/>.
/// </summary>
public static class PakRepacker
{
    public static void Build(
        PakAssetSource source,
        string outputPakPath,
        PakVersion version = PakVersion.V11,
        PakCompression[]? compression = null,
        byte[]? aesKey = null)
    {
        using var outputStream = File.Create(outputPakPath);

        var builder = new PakBuilder();
        if (aesKey != null) builder = builder.Key(aesKey);
        if (compression != null) builder = builder.Compression(compression);

        using var writer = builder.Writer(outputStream, version, source.MountPoint, pathHashSeed: 0);

        foreach (var entry in source.ListAllEntries())
        {
            var bytes = source.TryGetExtractedPath(entry, out var tempPath)
                ? File.ReadAllBytes(tempPath)
                : source.ReadOriginalBytes(entry);

            writer.WriteFile(entry, bytes);
        }

        writer.WriteIndex();
    }
}
