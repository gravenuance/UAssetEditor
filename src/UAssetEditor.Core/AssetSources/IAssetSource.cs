using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Provides access to a collection of .uasset files. Implementations decide where the
/// files physically live (a loose folder, or a .pak archive) and, since only the
/// implementation knows what its own "path" actually resolves to on disk, how to back
/// one up before overwriting it.
/// </summary>
public interface IAssetSource
{
    /// <summary>Enumerates the full paths of every .uasset file this source exposes.</summary>
    IEnumerable<string> EnumerateAssetPaths();

    UAsset OpenAsset(string assetPath, EngineVersion engineVersion, Usmap? mappings);

    void SaveAsset(UAsset asset, string assetPath, bool createBackup, string? backupFolder);
}
