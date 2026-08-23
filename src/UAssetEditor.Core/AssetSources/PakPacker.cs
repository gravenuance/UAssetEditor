using UAssetAPI;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Builds a brand-new .pak from an arbitrary loose folder on disk - the counterpart to
/// <see cref="PakRepacker"/>, which only ever rebuilds from an already-open
/// <see cref="PakAssetSource"/>. Every file under <paramref name="sourceFolder"/> (recursively)
/// becomes one pak entry, keyed by its path relative to that folder.
/// </summary>
public static class PakPacker
{
    public static void Build(
        string sourceFolder,
        string outputPakPath,
        string mountPoint = "../../../Game/",
        PakVersion version = PakVersion.V11,
        PakCompression[]? compression = null,
        byte[]? aesKey = null,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories).ToList();

        using var outputStream = File.Create(outputPakPath);

        var builder = new PakBuilder();
        if (aesKey != null) builder = builder.Key(aesKey);
        if (compression != null) builder = builder.Compression(compression);

        using var writer = builder.Writer(outputStream, version, mountPoint, pathHashSeed: 0);

        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceFolder, files[i]).Replace(Path.DirectorySeparatorChar, '/');
            writer.WriteFile(relativePath, File.ReadAllBytes(files[i]));

            progress?.Report((i + 1, files.Count));
        }

        writer.WriteIndex();
    }
}
