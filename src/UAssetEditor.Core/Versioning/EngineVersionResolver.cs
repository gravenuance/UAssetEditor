using System.Collections.ObjectModel;
using System.IO.Enumeration;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetEditor.Core.Games;

namespace UAssetEditor.Core.Versioning;

/// <summary>
/// A path pattern (matched with simple '*'/'?' wildcards against the full asset path)
/// that should be opened with a specific engine version rather than the default.
/// </summary>
public sealed record EngineVersionOverride(string PathPattern, EngineVersion Version);

/// <summary>
/// Resolves which <see cref="EngineVersion"/> to use when opening a given asset.
/// UAssetAPI cannot always infer the engine version uniquely from the file header alone,
/// so batches spanning multiple UE versions need a default plus per-path overrides.
/// </summary>
public sealed class EngineVersionResolver
{
    public EngineVersion DefaultVersion { get; set; } = EngineVersion.VER_UE4_27;

    /// <summary>The game the assets belong to, handed to every open so its own post-open step runs. The version and AES key it implies are applied by whoever builds this resolver.</summary>
    public Game Game { get; set; } = Game.None;

    /// <summary>Mappings file for games using unversioned properties. Optional.</summary>
    public Usmap? Mappings { get; set; }

    public Collection<EngineVersionOverride> Overrides { get; } = new();

    public EngineVersion Resolve(string assetPath)
    {
        foreach (var over in Overrides)
        {
            if (FileSystemName.MatchesSimpleExpression(over.PathPattern, assetPath))
                return over.Version;
        }

        return DefaultVersion;
    }
}
