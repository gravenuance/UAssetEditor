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

    /// <summary>Paths whose save throws, as a format that refuses an edit does.</summary>
    public HashSet<string> RefusedSaves { get; } = [];

    public IEnumerable<string> EnumerateAssetPaths() => _assets.Keys;

    public UAsset OpenAsset(string assetPath, EngineVersion engineVersion, Usmap? mappings, UAssetEditor.Core.Games.Game game) =>
        _assets.TryGetValue(assetPath, out var asset) ? asset : throw new InvalidDataException($"cannot parse {assetPath}");

    public void SaveAsset(UAsset asset, string assetPath, bool createBackup, string? backupFolder)
    {
        if (RefusedSaves.Contains(assetPath)) throw new InvalidDataException($"save refused for {assetPath}");
        SaveCount++;
    }
}
