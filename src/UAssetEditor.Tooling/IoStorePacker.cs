using UAssetEditor.Core.AssetSources.IoStore;
using UAssetEditor.Core.Games.Rivals;

namespace UAssetEditor.Tooling;

/// <summary>What to build: the loose folder, the .utoc to write, and the retoc engine version string (see <c>EngineVersionMapping</c>).</summary>
/// <param name="Patches">The Marvel Rivals asset patches to apply to a copy first, or null for none; they need <paramref name="UsmapPath"/>.</param>
internal sealed record IoStorePackRequest(
    string SourceFolder,
    string Output,
    string RetocVersion,
    byte[]? AesKey,
    RetocZenOptions ZenOptions,
    KawaiiPhysicsPortOptions? Patches,
    string? UsmapPath);

/// <summary>Builds an IoStore .utoc/.ucas from a loose folder via retoc, optionally patching a temporary copy of it first.</summary>
internal static class IoStorePacker
{
    /// <param name="info">Receives retoc's output lines and one PATCHED line per patched asset.</param>
    /// <param name="warn">Receives warnings that do not stop the pack.</param>
    public static async Task PackAsync(IoStorePackRequest request, Action<string> info, Action<string> warn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(warn);

        // Checked before staging too, so a drive root is refused instead of copied.
        if (RetocDirectoryInputResolver.Resolve(request.SourceFolder) == null)
            throw new ArgException("Source folder is a drive root - pick a folder that isn't.");

        if (request.Patches == null)
        {
            await ConvertFolderToZenAsync(request, request.SourceFolder, info, cancellationToken).ConfigureAwait(false);
            return;
        }

        var usmapPath = request.UsmapPath
            ?? throw new ArgException("The Marvel Rivals asset patches need a usmap for packages that cannot be read without it.");

        // The patches rewrite packages in place, so they run on a copy and the source folder is never touched.
        var stageRoot = Path.Combine(Path.GetTempPath(), "uacli-pack-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stagedSource = Path.Combine(stageRoot, new DirectoryInfo(request.SourceFolder.TrimEnd('\\', '/')).Name);
            CopyDirectory(request.SourceFolder, stagedSource, cancellationToken);
            PatchRivalsAssets(stagedSource, usmapPath, request.Patches, info, warn);
            await ConvertFolderToZenAsync(request, stagedSource, info, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteStage(stageRoot, warn);
        }
    }

    private static Task ConvertFolderToZenAsync(IoStorePackRequest request, string folder, Action<string> info, CancellationToken cancellationToken)
    {
        var retocInput = RetocDirectoryInputResolver.Resolve(folder)
            ?? throw new ArgException("Source folder is a drive root - pick a folder that isn't.");
        return RetocProcess.ConvertToZenAsync(
            retocInput, request.Output, request.RetocVersion, request.AesKey, request.ZenOptions, onOutputLine: info, cancellationToken);
    }

    private static void PatchRivalsAssets(string folder, string usmapPath, KawaiiPhysicsPortOptions options, Action<string> info, Action<string> warn)
    {
        IReadOnlyList<PatchedAsset> patched;
        try
        {
            patched = RivalsAssetPatcher.PatchDirectory(folder, usmapPath, options, warn);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        {
            throw new ArgException($"Patching failed, nothing was packed: {ex.Message}", ex);
        }

        foreach (var asset in patched)
        {
            var r = asset.Result;
            info($"PATCHED {asset.RelativePath.Replace('\\', '/')}: visited={r.VisitedAnimNodes} ported={r.PortedAnimNodes} skipped={r.SkippedExistingChains} hiddenLods={r.PatchedDefaultHiddenMaterialLods}");
        }
        info($"-- patched {patched.Count} asset(s)");
    }

    /// <summary>Copies a folder tree; the copies are made writable, since the patches rewrite them and read-only sources are common.</summary>
    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copy = Path.Combine(destination, Path.GetRelativePath(source, file));
            File.Copy(file, copy);
            File.SetAttributes(copy, FileAttributes.Normal);
        }
    }

    /// <summary>Removes the staging folder; a failure only warns, so it can't hide the pack's own result.</summary>
    private static void DeleteStage(string stageRoot, Action<string> warn)
    {
        try
        {
            if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warn($"could not delete the staging folder {stageRoot}: {ex.Message}");
        }
    }
}
