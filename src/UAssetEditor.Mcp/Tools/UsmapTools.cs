using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UAssetEditor.Core.Games;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Tools for the per-game store of the current usmap. A game patch changes the schema, so the newest stored build
/// is what every tool with a game uses when no usmap is given.</summary>
[McpServerToolType]
internal sealed class UsmapTools(MappingsStore store)
{
    [McpServerTool(Name = "usmap_list", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Lists the usmaps in the store, highest game build first (one without a build comes last): game, build and path. The first one for a game is what the other tools use when given that game and no usmap.")]
    public Task<UsmapListResult> UsmapListAsync(
        [Description("Only this game: rivals, ff7r or stellarblade. Omit for all.")] string? game = null,
        CancellationToken cancellationToken = default)
    {
        var profiles = ParseGame(game) is { } one ? [one] : GameProfile.All;
        var usmaps = new List<UsmapDto>();
        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            usmaps.AddRange(store.List(profile).Select(entry => new UsmapDto(profile.CliName, entry.Build, entry.Path)));
        }

        return Task.FromResult(new UsmapListResult(store.Root, usmaps));
    }

    [McpServerTool(Name = "usmap_add", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description("Copies a dumped .usmap (Dumper-7) into the store for a game and makes it the newest if its build is highest. The build comes from the Dumper-7 file name, or from build. The file must load as a usmap. Adding a build that is already stored, or a second usmap without a build, is refused unless force is true, which replaces the stored one. Run it after each game patch.")]
    public Task<UsmapAddResult> UsmapAddAsync(
        [Description("Absolute path of the .usmap to add.")] string file,
        [Description("Game it belongs to: rivals, ff7r or stellarblade.")] string game,
        [Description("Game build number, when the file name does not carry it (the digits after the engine version's '-' in a Dumper-7 name).")] int? build = null,
        [Description("Replace an already stored usmap of the same build (or the game's one usmap without a build).")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var path = ToolPaths.ExistingFile(file, nameof(file), ".usmap");
        var profile = ParseGame(game) ?? throw new McpException("game is required.");
        cancellationToken.ThrowIfCancellationRequested();

        StoredMappings stored;
        try
        {
            stored = store.Add(path, profile, build, force);
        }
        catch (MappingsStoreException ex)
        {
            // The store words its advice for uacli's options; here they are parameters.
            throw new McpException(ex.Message.Replace("--force", "force=true", StringComparison.Ordinal).Replace("--build N", "build", StringComparison.Ordinal), ex);
        }

        var note = stored.Build == null && profile.Game == Game.MarvelRivals
            ? "Stored without a build, so it cannot be told when it is out of date with the game: add Dumper-7's file as named, or pass build."
            : "";
        return Task.FromResult(new UsmapAddResult(new UsmapDto(profile.CliName, stored.Build, stored.Path), note));
    }

    private static GameProfile? ParseGame(string? game)
    {
        var text = ToolInput.Optional(game, nameof(game), 40);
        if (text == null) return null;
        return GameProfile.FindByCliName(text)
            ?? throw new McpException($"Unknown game '{Untrusted.Clean(text, 40)}': expected one of {GameProfile.ValidNames}.");
    }
}
