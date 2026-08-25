using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Wraps exactly one loose .uasset file so it can be browsed/edited through the same
/// <see cref="IAssetSource"/>/<see cref="AssetWorkspace"/> pipeline as a whole folder or
/// pak. Asset-path identity is just the file's name (no parent folders), so its tree
/// shows "&lt;filename&gt; -&gt; Exports -&gt; ..." with nothing above it, matching how a
/// folder/pak root's own top-level entries look.
/// </summary>
public sealed class SingleFileAssetSource : IAssetSource
{
    private readonly string _filePath;
    private readonly string _fileName;

    public SingleFileAssetSource(string filePath)
    {
        _filePath = filePath;
        _fileName = Path.GetFileName(filePath);
    }

    public IEnumerable<string> EnumerateAssetPaths() => [_fileName];

    public UAsset OpenAsset(string assetPath, EngineVersion engineVersion, Usmap? mappings) =>
        ResilientAssetLoader.Open(_filePath, engineVersion, mappings);

    public void SaveAsset(UAsset asset, string assetPath, bool createBackup, string? backupFolder)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (createBackup)
            File.Copy(_filePath, BackupPathResolver.Resolve(_filePath, backupFolder), overwrite: true);

        asset.Write(_filePath);
    }
}
