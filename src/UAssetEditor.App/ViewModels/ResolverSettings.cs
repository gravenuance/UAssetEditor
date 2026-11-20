using System.IO;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.Games;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.App.ViewModels;

/// <summary>
/// The settings a workspace's <see cref="EngineVersionResolver"/> is built from, and whether its usmap actually loaded. The usmap
/// path is kept as the full path of its trimmed text (none when blank), so the same file spelt differently is the same input.
/// </summary>
public sealed record ResolverSettings
{
    private ResolverSettings(EngineVersion version, Game game, string? usmapPath, bool mappingsLoaded)
    {
        Version = version;
        Game = game;
        UsmapPath = usmapPath;
        MappingsLoaded = mappingsLoaded;
    }

    public EngineVersion Version { get; }

    public Game Game { get; }

    /// <summary>The usmap's full path, or null when none is set.</summary>
    public string? UsmapPath { get; }

    /// <summary>Whether a usmap was set and loaded; false for a path whose file was missing, which a later reload may find.</summary>
    public bool MappingsLoaded { get; }

    public static ResolverSettings Create(EngineVersion version, Game game, string? usmapPath, bool mappingsLoaded) =>
        new(version, game, NormalisePath(usmapPath), mappingsLoaded);

    /// <summary>Whether a resolver built from <paramref name="other"/> would behave like one built from these settings.</summary>
    public bool SameInputs(ResolverSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Version == other.Version
            && Game == other.Game
            && string.Equals(UsmapPath, other.UsmapPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalisePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var trimmed = path.Trim();
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed; // not a usable path: kept as typed, and the load reports it
        }
    }
}

/// <summary>A resolver together with the settings it was built from.</summary>
public sealed record ResolverBuild(EngineVersionResolver Resolver, ResolverSettings Applied);
