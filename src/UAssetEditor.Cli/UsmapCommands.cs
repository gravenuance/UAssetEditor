using System.Globalization;
using UAssetEditor.Core.Games;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.Cli;

/// <summary>The <c>usmap</c> verb: the per-game store of the current usmap (see <see cref="MappingsStore"/>).</summary>
internal static class UsmapCommands
{
    public static int Run(ArgReader args)
    {
        var action = args.Positional(0) ?? throw new ArgException("Missing usmap action: add, list, path, diff or triage.");
        return action switch
        {
            "add" => Add(args),
            "list" => List(args),
            "path" => PrintPath(args),
            "diff" => Diff(args),
            "triage" => Triage(args),
            _ => throw new ArgException($"Unknown usmap action '{action}' (expects add, list, path, diff or triage)."),
        };
    }

    private static int Diff(ArgReader args)
    {
        var (oldPath, newPath) = ResolvePair(args);
        var diff = UsmapSchemaDiff.Compare(AssetIo.LoadMappings(oldPath, "--from-usmap"), AssetIo.LoadMappings(newPath, "--usmap"));
        foreach (var change in diff.Changed)
        {
            Console.WriteLine($"CHANGED\t{change.Name}\t{change.OldCount}->{change.NewCount}");
            foreach (var field in change.Added) Console.WriteLine($"\t+ {field}");
            foreach (var field in change.Removed) Console.WriteLine($"\t- {field}");
            if (change.OldSuper != change.NewSuper) Console.WriteLine($"\tsuper {change.OldSuper} -> {change.NewSuper}");
            if (change.LayoutOnly) Console.WriteLine("\t(same fields, new positions or sizes)");
        }
        foreach (var name in diff.Removed) Console.WriteLine($"REMOVED\t{name}");
        Console.Error.WriteLine($"Schemas: {diff.Changed.Count} changed, {diff.Added.Count} added, {diff.Removed.Count} removed.");
        return 0;
    }

    private static int Triage(ArgReader args)
    {
        var inputs = args.PositionalsFrom(1);
        if (inputs.Count == 0) throw new ArgException("Missing argument: one or more .uasset files or folders (or - to read paths from stdin).");
        var files = ExpandInputs(inputs);
        if (files.Count == 0) throw new ArgException("No .uasset files under the given paths.");

        var version = AssetIo.ResolveVersion(args);
        var game = AssetIo.ResolveGame(args);
        var degree = AssetIo.ResolveJobs(args);
        var (oldPath, newPath) = ResolvePair(args);
        var oldMappings = AssetIo.LoadMappings(oldPath, "--from-usmap");
        var newMappings = AssetIo.LoadMappings(newPath, "--usmap");

        var results = new TriageResult[files.Count];
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = degree },
            i => results[i] = UsmapTriage.TriageFile(files[i], version, oldMappings, newMappings, game));

        foreach (var result in results) Console.WriteLine($"{UsmapTriage.Label(result.Status)}\t{result.Path}\t{result.Detail}");

        var counts = results.GroupBy(result => result.Status).OrderBy(group => group.Key)
            .Select(group => $"{group.Count()} {UsmapTriage.Label(group.Key)}");
        Console.Error.WriteLine($"{files.Count} assets: {string.Join(", ", counts)}.");
        if (results.Any(result => result.Status is TriageStatus.Misread or TriageStatus.NewFails))
            Console.Error.WriteLine("Any MISREAD or NEW-FAILS asset built for the old usmap needs rebuilding for the new one (rebase on the game's current copy, or to-unversioned --from-usmap).");
        return results.Any(result => result.Status == TriageStatus.Error) ? 1 : 0;
    }

    // Files as given, folders searched for .uasset files, "-" read as one path per line from stdin.
    private static List<string> ExpandInputs(IReadOnlyList<string> inputs)
    {
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            if (input == "-")
            {
                while (Console.In.ReadLine() is { } line)
                {
                    if (line.Trim() is { Length: > 0 } path) AddFile(files, path);
                }
            }
            else if (Directory.Exists(input))
            {
                files.UnionWith(Directory.EnumerateFiles(input, "*.uasset", SearchOption.AllDirectories).Select(Path.GetFullPath));
            }
            else
            {
                AddFile(files, input);
            }
        }
        return [.. files];
    }

    private static void AddFile(SortedSet<string> files, string path)
    {
        if (!File.Exists(path)) throw new ArgException($"File not found: {path}");
        files.Add(Path.GetFullPath(path));
    }

    /// <summary>
    /// The new usmap (--usmap, else the newest stored for --game) and the old one (--from-usmap, else the stored usmap just
    /// before the new one, or the newest stored when the new one is from outside the store).
    /// </summary>
    private static (string OldPath, string NewPath) ResolvePair(ArgReader args)
    {
        if (args.Flag("from-usmap")) throw new ArgException("--from-usmap needs a value: the usmap the assets were built with.");
        var newPath = AssetIo.ResolveUsmapPath(args)
            ?? throw new ArgException("Pass --usmap <new.usmap>, or --game <g> with a stored usmap.");
        var oldPath = args.Option("from-usmap") ?? PreviousStored(args, newPath);
        if (string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgException($"--from-usmap and --usmap are the same file ({newPath}).");
        return (oldPath, newPath);
    }

    private static string PreviousStored(ArgReader args, string newPath)
    {
        var game = AssetIo.ResolveProfile(args)
            ?? throw new ArgException("Pass --from-usmap <old.usmap>, or --game <g> to use the stored usmap before the newest.");
        var entries = MappingsStore.Default().List(game);
        var newFull = Path.GetFullPath(newPath);
        var current = -1; // a usmap from outside the store: compare with the newest stored one
        for (var i = 0; i < entries.Count && current < 0; i++)
        {
            if (string.Equals(Path.GetFullPath(entries[i].Path), newFull, StringComparison.OrdinalIgnoreCase)) current = i;
        }
        var previous = current + 1 < entries.Count ? entries[current + 1] : null;
        if (previous == null)
            throw new ArgException($"No usmap older than {newPath} is stored for {game.CliName}; pass --from-usmap <old.usmap>.");
        Console.Error.WriteLine($"Comparing with stored usmap {previous.Path} ({DescribeBuild(previous)}).");
        return previous.Path;
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

        Console.WriteLine($"Stored {stored.Path} ({game.CliName}, {DescribeBuild(stored)}).");
        if (stored.Build == null && game.Game == Game.MarvelRivals)
            Console.Error.WriteLine("Warning: without a build, uacli cannot tell when this usmap is out of date with the game; add Dumper-7's file as named, or pass --build.");
        return 0;
    }

    /// <summary>"build N", or "no build" for a usmap stored without one.</summary>
    internal static string DescribeBuild(StoredMappings stored) =>
        stored.Build is { } build ? $"build {build.ToString(CultureInfo.InvariantCulture)}" : "no build";

    private static int List(ArgReader args)
    {
        var store = MappingsStore.Default();
        var games = AssetIo.ResolveProfile(args) is { } one ? [one] : GameProfile.All;
        var any = false;
        foreach (var game in games)
        {
            foreach (var entry in store.List(game))
            {
                Console.WriteLine($"{game.CliName}\t{entry.Build?.ToString(CultureInfo.InvariantCulture) ?? "-"}\t{entry.Path}");
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
