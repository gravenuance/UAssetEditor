using System.Globalization;
using UAssetEditor.Core.Games;

namespace UAssetEditor.Cli;

/// <summary>The <c>usmap</c> verb: the per-game store of the current usmap (see <see cref="MappingsStore"/>).</summary>
internal static class UsmapCommands
{
    public static int Run(ArgReader args)
    {
        var action = args.Positional(0) ?? throw new ArgException("Missing usmap action: add, list or path.");
        return action switch
        {
            "add" => Add(args),
            "list" => List(args),
            "path" => PrintPath(args),
            _ => throw new ArgException($"Unknown usmap action '{action}' (expects add, list or path)."),
        };
    }

    private static int Add(ArgReader args)
    {
        var file = args.Positional(1, "file");
        var game = RequireGame(args);
        var force = args.Switch("force");
        StoredMappings stored;
        try
        {
            stored = MappingsStore.Default().Add(file, game, ParseBuild(args), force);
        }
        catch (MappingsStoreException ex)
        {
            throw new ArgException(ex.Message, ex);
        }

        Console.WriteLine($"Stored {stored.Path} ({game.CliName} build {stored.Build}).");
        return 0;
    }

    private static int List(ArgReader args)
    {
        var store = MappingsStore.Default();
        var games = AssetIo.ResolveProfile(args) is { } one ? [one] : GameProfile.All;
        var any = false;
        foreach (var game in games)
        {
            foreach (var entry in store.List(game))
            {
                Console.WriteLine($"{game.CliName}\t{entry.Build}\t{entry.Path}");
                any = true;
            }
        }
        if (!any) Console.Error.WriteLine($"No usmaps stored in {store.Root}.");
        return 0;
    }

    private static int PrintPath(ArgReader args)
    {
        var game = RequireGame(args);
        var latest = MappingsStore.Default().Latest(game)
            ?? throw new ArgException($"No usmap stored for {game.CliName}. Dump one (Dumper-7) and add it with 'uacli usmap add <file> --game {game.CliName}'.");
        Console.WriteLine(latest.Path);
        return 0;
    }

    private static GameProfile RequireGame(ArgReader args) =>
        AssetIo.ResolveProfile(args) ?? throw new ArgException($"--game is required (one of: {GameProfile.ValidNames}).");

    private static int? ParseBuild(ArgReader args)
    {
        if (args.Flag("build")) throw new ArgException("--build needs a value: the game build number, e.g. 3939753.");
        if (args.Option("build") is not { } text) return null;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var build) && build > 0
            ? build
            : throw new ArgException($"--build must be a positive whole number, not '{text}'.");
    }
}
