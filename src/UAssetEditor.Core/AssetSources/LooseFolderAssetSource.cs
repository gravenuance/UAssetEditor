using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Reads and writes .uasset files that sit as loose files under a root folder
/// (as opposed to being packed inside a .pak/.utoc archive).
/// </summary>
public sealed class LooseFolderAssetSource : IAssetSource
{
    private readonly string _rootPath;

    public LooseFolderAssetSource(string rootPath)
    {
        _rootPath = rootPath;
    }

    public IEnumerable<string> EnumerateAssetPaths() =>
        Directory.EnumerateFiles(_rootPath, "*.uasset", SearchOption.AllDirectories);

    public UAsset OpenAsset(string assetPath, EngineVersion engineVersion, Usmap? mappings) =>
        ResilientAssetLoader.Open(assetPath, engineVersion, mappings);

    public void SaveAsset(UAsset asset, string assetPath, bool createBackup, string? backupFolder)
    {
        if (createBackup)
            File.Copy(assetPath, BackupPathResolver.Resolve(assetPath, backupFolder), overwrite: true);

        asset.Write(assetPath);
    }
}
