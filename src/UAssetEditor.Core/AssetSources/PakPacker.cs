using UAssetAPI;
using UAssetEditor.Core.AssetSources.PakWorker;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Builds a brand-new .pak from an arbitrary loose folder on disk - the counterpart to
/// <see cref="PakRepacker"/>, which only ever rebuilds from an already-open
/// <see cref="PakAssetSource"/>. Every file under <paramref name="sourceFolder"/> (recursively)
/// becomes one pak entry, keyed by its path relative to that folder; the pak is written in the given
/// <see cref="PakVariant"/>, which must be the game's own or its readers cannot open it. Writes go through the
/// out-of-process worker like every other repak call (see <see cref="PakWorkerProcess"/>) -
/// a crash mid-build is not resumable (a pak writer session can't append after the fact), so
/// it fails this whole attempt cleanly, discarding the partial output, rather than the app.
/// </summary>
public static class PakPacker
{
    /// <summary>The mount point for a pak whose project folder is unknown.</summary>
    public const string GenericMountPoint = "../../../Game/";

    /// <summary>The pak version every front end writes unless told otherwise.</summary>
    public const PakVersion DefaultVersion = PakVersion.V11;

    /// <summary>
    /// The mount point every front end assumes for a pak built from <paramref name="sourceFolder"/>: the folder is taken to be
    /// named after the game's project folder ("../../../&lt;name&gt;/"). Null when the path has no folder name.
    /// </summary>
    public static string? DefaultMountPoint(string sourceFolder)
    {
        ArgumentNullException.ThrowIfNull(sourceFolder);
        var trimmed = sourceFolder.TrimEnd('\\', '/');
        var name = trimmed.Length == 0 ? "" : new DirectoryInfo(trimmed).Name;
        return name.Length == 0 ? null : $"../../../{name}/";
    }

    public static PakBulkResult Build(
        string sourceFolder,
        string outputPakPath,
        string mountPoint = GenericMountPoint,
        PakVersion version = DefaultVersion,
        PakCompression[]? compression = null,
        byte[]? aesKey = null,
        PakVariant variant = PakVariant.Standard,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories).ToList();

        using var writer = new PakWriterHandle(PakWorkerProcess.Shared);
        try
        {
            writer.OpenAsync(outputPakPath, mountPoint, version, compression, aesKey, variant, cancellationToken).GetAwaiter().GetResult();

            for (var i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(sourceFolder, files[i]).Replace(Path.DirectorySeparatorChar, '/');
                var bytes = File.ReadAllBytes(files[i]);
                writer.WriteFileAsync(relativePath, bytes, cancellationToken).GetAwaiter().GetResult();

                progress?.Report((i + 1, files.Count));
            }

            writer.WriteIndexAsync(cancellationToken).GetAwaiter().GetResult();
            return new PakBulkResult { SucceededCount = files.Count };
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
