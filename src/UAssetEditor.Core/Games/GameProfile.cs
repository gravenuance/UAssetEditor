using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.Games.Ff7r;

namespace UAssetEditor.Core.Games;

/// <summary>
/// What selecting a game fills in: the engine version its packages are cooked with, the AES key its paks are
/// encrypted with, and (for a game that has one) a step that runs on every package right after it is opened.
/// Both keys are the publicly known community keys for these games.
/// </summary>
public sealed record GameProfile(Game Game, string CliName, string DisplayName, EngineVersion EngineVersion, string AesKeyHex)
{
    private static readonly GameProfile[] Profiles =
    [
        new(Game.MarvelRivals, "rivals", "Marvel Rivals", EngineVersion.VER_UE5_3,
            "0x0C263D8C22DCB085894899C3A3796383E9BF9DE0CBFB08C9BF2DEF2E84F29D74"),
        new(Game.FinalFantasy7Remake, "ff7r", "Final Fantasy VII Remake", EngineVersion.VER_UE4_18,
            "0x23989837645C9D28BA58072B2076E895B853A7C9E1C5591B814C4FD2A2D7B782"),
    ];

    /// <summary>Every game that has a profile, in menu order (<see cref="Game.None"/> has none).</summary>
    public static IReadOnlyList<GameProfile> All => Profiles;

    /// <summary>The CLI names joined for messages, e.g. "rivals, ff7r".</summary>
    public static string ValidNames => string.Join(", ", Profiles.Select(p => p.CliName));

    /// <summary>The profile for <paramref name="game"/>, or null for <see cref="Game.None"/>.</summary>
    public static GameProfile? For(Game game) => game switch
    {
        Game.None => null,
        Game.MarvelRivals => Profiles[0],
        Game.FinalFantasy7Remake => Profiles[1],
        _ => throw new ArgumentOutOfRangeException(nameof(game), game, "Unknown game."),
    };

    /// <summary>The profile whose CLI name is <paramref name="name"/> (case-insensitive), or null when there is none.</summary>
    public static GameProfile? FindByCliName(string? name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.CliName, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The engine version to open with: an explicit choice wins, then the game's, then <see cref="EngineVersion.VER_UE4_27"/>.</summary>
    public static EngineVersion ResolveEngineVersion(GameProfile? profile, EngineVersion? explicitVersion) =>
        explicitVersion ?? profile?.EngineVersion ?? EngineVersion.VER_UE4_27;

    /// <summary>The AES key (hex) to use: an explicit key wins, then the game's, else an empty string.</summary>
    public static string ResolveAesKeyHex(GameProfile? profile, string? explicitKeyHex) =>
        !string.IsNullOrWhiteSpace(explicitKeyHex) ? explicitKeyHex : profile?.AesKeyHex ?? string.Empty;

    /// <summary>
    /// Runs this game's own step on a freshly opened package and returns a warning for everything it had to leave alone.
    /// Never throws for a package it can't make sense of: the package then simply stays as it was opened.
    /// </summary>
    public IReadOnlyList<string> PostOpen(UAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return Game switch
        {
            Game.None => [],
            Game.MarvelRivals => [],
            Game.FinalFantasy7Remake => Ff7rDataObjects.Install(asset),
            _ => throw new InvalidOperationException($"Unknown game {Game}."),
        };
    }
}
