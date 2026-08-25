using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Reads and writes .uasset files that sit as loose files under a root folder
/// (as opposed to being packed inside a .pak/.utoc archive). Asset-path identity is
/// root-relative (e.g. "Game/Content/Foo.uasset"), mirroring how <see cref="PakAssetSource"/>
/// already treats its paths as pak-relative, so a loose-folder tree and a pak tree branch
/// identically for an equivalent layout. Resolved to an absolute path only where real
/// file I/O actually happens.
/// </summary>
public sealed class LooseFolderAssetSource : IAssetSource
{
    private readonly string _rootPath;

    public LooseFolderAssetSource(string rootPath)
    {
        _rootPath = rootPath;
    }

    public IEnumerable<string> EnumerateAssetPaths() =>
        Directory.EnumerateFiles(_rootPath, "*.uasset", SearchOption.AllDirectories)
            .Select(ToRelativePath);

    public UAsset OpenAsset(string assetPath, EngineVersion engineVersion, Usmap? mappings)
    {
        ArgumentNullException.ThrowIfNull(assetPath);
        return ResilientAssetLoader.Open(ToAbsolutePath(assetPath), engineVersion, mappings);
    }

    public void SaveAsset(UAsset asset, string assetPath, bool createBackup, string? backupFolder)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(assetPath);

        var absolutePath = ToAbsolutePath(assetPath);

        if (createBackup)
            File.Copy(absolutePath, BackupPathResolver.Resolve(absolutePath, backupFolder), overwrite: true);

        asset.Write(absolutePath);
    }

    private string ToRelativePath(string absolutePath) =>
        Path.GetRelativePath(_rootPath, absolutePath).Replace(Path.DirectorySeparatorChar, '/');

    private string ToAbsolutePath(string relativePath) =>
        Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
