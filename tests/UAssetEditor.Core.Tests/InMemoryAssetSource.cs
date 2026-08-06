using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.Core.Tests;

/// <summary>An <see cref="IAssetSource"/> backed by already-constructed in-memory assets, for tests that don't need real files.</summary>
internal sealed class InMemoryAssetSource : IAssetSource
{
    private readonly Dictionary<string, UAsset> _assets;

    public InMemoryAssetSource(Dictionary<string, UAsset> assets) => _assets = assets;

    public int SaveCount { get; private set; }

    public IEnumerable<string> EnumerateAssetPaths() => _assets.Keys;

    public UAsset OpenAsset(string assetPath, EngineVersion engineVersion, Usmap? mappings) => _assets[assetPath];

    public void SaveAsset(UAsset asset, string assetPath, bool createBackup, string? backupFolder) => SaveCount++;
}
