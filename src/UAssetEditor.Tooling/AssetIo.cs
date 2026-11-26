using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Games;
using UAssetEditor.Core.Games.Rivals;
using UAssetEditor.Core.PropertyAccess;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.Tooling;

/// <summary>Shared open/save/export-resolution plumbing every command needs, kept out of each command so the interesting logic isn't buried under it.</summary>
internal static class AssetIo
{
    /// <summary>
    /// Same shape the app's own batch-edit tab saves/loads (<c>EditRule</c> is already
    /// JSON-polymorphic on a "kind" discriminator) - a ruleset written by one reads back in
    /// the other. Adds <see cref="JsonStringEnumConverter"/> on top, which the app doesn't
    /// use (its enums serialize as bare numbers): it still reads an app-authored file fine
    /// (the converter accepts numbers on input) while making a hand-authored ruleset - the
    /// normal way to drive this CLI - readable ("Contains" instead of "0").
    /// </summary>
    public static readonly JsonSerializerOptions RuleSetJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The game named by --game, or <see cref="Game.None"/> when it isn't given.</summary>
    public static Game ResolveGame(ArgReader args) => ResolveProfile(args)?.Game ?? Game.None;

    /// <summary>The profile named by --game, or null when it isn't given.</summary>
    public static GameProfile? ResolveProfile(ArgReader args)
    {
        var name = args.Option("game");
        if (name == null)
            return args.Flag("game") ? throw new ArgException($"--game needs a value (one of: {GameProfile.ValidNames}).") : null;
        return GameProfile.FindByCliName(name)
            ?? throw new ArgException($"Unknown --game '{name}' (expects one of: {GameProfile.ValidNames}).");
    }

    /// <summary>--version if given, else the --game profile's engine version, else VER_UE4_27.</summary>
    public static EngineVersion ResolveVersion(ArgReader args)
    {
        var profile = ResolveProfile(args);
        var text = args.Option("version");
        if (text == null) return GameProfile.ResolveEngineVersion(profile, null);
        return Enum.TryParse<EngineVersion>(text, ignoreCase: true, out var version)
            ? GameProfile.ResolveEngineVersion(profile, version)
            : throw new ArgException($"Unknown --version '{text}' (expects a UAssetAPI.EngineVersion name, e.g. VER_UE4_27).");
    }

    // Loading a usmap is most of a small run's cost, so each is loaded once per process and every asset gets its own scope of it.
    // One entry per file: the file's size and write time say which version of it the entry holds.
    private sealed record LoadedUsmap(long Length, long WriteTicks, Lazy<Usmap> Usmap);

    private static readonly ConcurrentDictionary<string, LoadedUsmap> LoadedMappings = new(StringComparer.OrdinalIgnoreCase);

    // The state of the game's logs each usmap was last checked against: one check per state, however many assets a --plan run opens.
    private static readonly ConcurrentDictionary<string, string> StaleChecked = new(StringComparer.OrdinalIgnoreCase);

    public static Usmap? ResolveMappings(ArgReader args, string option = "usmap")
    {
        var path = ResolveUsmapPath(args, option);
        return path == null ? null : LoadMappings(path, $"--{option}").CreateAssetScope();
    }

    /// <summary>The engine version, usmap and game every asset of a run opens with.</summary>
    public static EngineVersionResolver ResolveVersions(ArgReader args) =>
        new() { DefaultVersion = ResolveVersion(args), Mappings = ResolveMappings(args), Game = ResolveGame(args) };

    /// <summary>
    /// The usmap at <paramref name="path"/>, loaded once per process and shared: give each asset its own <c>CreateAssetScope()</c>.
    /// A file replaced in place (<c>usmap add --force</c> in a long-running server) is loaded again.
    /// </summary>
    public static Usmap LoadMappings(string path, string label)
    {
        if (!File.Exists(path)) throw new ArgException($"{label} file not found: {path}");
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        var length = info.Length;
        var ticks = info.LastWriteTimeUtc.Ticks;

        // A changed file replaces its entry, so the old usmap can be collected once nothing scopes it any more.
        var entry = LoadedMappings.AddOrUpdate(
            full,
            _ => new LoadedUsmap(length, ticks, new Lazy<Usmap>(() => new Usmap(full))),
            (_, existing) => existing.Length == length && existing.WriteTicks == ticks
                ? existing
                : new LoadedUsmap(length, ticks, new Lazy<Usmap>(() => new Usmap(full))));
        return entry.Usmap.Value;
    }

    /// <summary>How many loaded usmaps the cache holds for <paramref name="path"/>: one while the file is unchanged, and still one after it is replaced.</summary>
    internal static int CachedMappingsFor(string path)
    {
        var full = Path.GetFullPath(path);
        return LoadedMappings.Keys.Count(key => key.StartsWith(full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>--jobs, else one worker per CPU.</summary>
    public static int ResolveJobs(ArgReader args)
    {
        if (args.Option("jobs") is not { } text) return Environment.ProcessorCount;
        return int.TryParse(text, out var jobs) && jobs > 0 ? jobs : throw new ArgException($"--jobs must be a positive number, not '{text}'.");
    }

    /// <summary>
    /// The usmap to use: the explicit option, else (for --usmap only) the newest one stored for --game, else null (with a
    /// warning when --game was given). Rivals gets a warning when the usmap is from a different build than the game last ran.
    /// </summary>
    public static string? ResolveUsmapPath(ArgReader args, string option = "usmap")
    {
        var explicitPath = args.Option(option);
        if (explicitPath != null)
        {
            if (option == "usmap") WarnIfStale(ResolveProfile(args), explicitPath, args);
            return explicitPath;
        }

        if (option != "usmap" || ResolveProfile(args) is not { } profile) return null;
        return args.StoredUsmaps.GetOrAdd(profile.CliName, _ => new Lazy<string?>(() => UseStoredUsmap(profile, args))).Value;
    }

    /// <summary>"build N", or "no build" for a usmap stored without one.</summary>
    public static string DescribeBuild(StoredMappings stored) =>
        stored.Build is { } build ? $"build {build.ToString(CultureInfo.InvariantCulture)}" : "no build";

    /// <summary>
    /// The newest usmap stored for <paramref name="profile"/> in <paramref name="args"/>' store, announced on its diagnostics. When
    /// there is none, says so and how to give one: assets of these games open without one and quietly parse to nothing.
    /// </summary>
    private static string? UseStoredUsmap(GameProfile profile, ArgReader args)
    {
        var store = args.Mappings;
        if (store.Latest(profile) is not { } stored)
        {
            args.Diagnostics.WriteLine(
                $"Warning: no usmap is given or stored for {profile.CliName} in {store.DirectoryFor(profile)}" +
                $" ({MappingsStore.RootEnvironmentVariable} sets the store); unversioned assets will read as empty." +
                $" Pass {args.UsmapHints.Pass}, or store one with {args.UsmapHints.Store(profile)}.");
            return null;
        }
        args.Diagnostics.WriteLine($"Using stored usmap {stored.Path} ({DescribeBuild(stored)}).");
        WarnIfStale(profile, stored.Path, args);
        return stored.Path;
    }

    private static void WarnIfStale(GameProfile? profile, string usmapPath, ArgReader args)
    {
        if (profile?.Game != Game.MarvelRivals) return;
        if (MappingsStore.ParseBuild(Path.GetFileName(usmapPath)) is not { } usmapBuild) return;
        if (!NeedsStaleCheck(Path.GetFullPath(usmapPath), args.GameLogs)) return;
        if (RivalsLogs.InstalledBuild(args.GameLogs) is not { } installed || installed == usmapBuild) return;
        args.Diagnostics.WriteLine(
            $"Warning: usmap is build {usmapBuild} but Marvel Rivals last ran build {installed};" +
            $" dump a new usmap (Dumper-7) and store it with {args.UsmapHints.Store(profile)}.");
    }

    /// <summary>
    /// True when <paramref name="usmapPath"/> has not been checked against the game's newest log in its present state, and notes that
    /// it now has been: a long-running host sees the game patched and run again, and must warn then, not only the first time.
    /// </summary>
    private static bool NeedsStaleCheck(string usmapPath, string gameLogs)
    {
        var stamp = GameLogStamp(gameLogs);
        while (true)
        {
            if (!StaleChecked.TryGetValue(usmapPath, out var seen))
            {
                if (StaleChecked.TryAdd(usmapPath, stamp)) return true;
            }
            else if (seen == stamp)
            {
                return false;
            }
            else if (StaleChecked.TryUpdate(usmapPath, stamp, seen))
            {
                return true;
            }
        }
    }

    /// <summary>Which log is newest and when it was last written; empty when there is none, so a first log appearing counts as a change.</summary>
    private static string GameLogStamp(string gameLogs)
    {
        try
        {
            return RivalsLogs.Newest(gameLogs) is { } log ? $"{log}|{File.GetLastWriteTimeUtc(log).Ticks}" : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>--aes if given, else the --game profile's key, else none.</summary>
    public static byte[]? ResolveAesKey(ArgReader args) => PakAesKey.Parse(GameProfile.ResolveAesKeyHex(ResolveProfile(args), args.Option("aes")));

    /// <summary>The --game profile's pak format, else the standard one.</summary>
    public static PakVariant ResolvePakVariant(ArgReader args) => GameProfile.ResolvePakVariant(ResolveProfile(args));

    public static PakVersion ResolvePakVersion(ArgReader args)
    {
        if (args.Option("pak-version") is not { } text) return PakPacker.DefaultVersion;
        return Enum.TryParse<PakVersion>(text, ignoreCase: true, out var version)
            ? version
            : throw new ArgException($"Unknown --pak-version '{text}' (expects a UAssetAPI.PakVersion name, e.g. V11).");
    }

    public static PakCompression[]? ResolveCompression(ArgReader args)
    {
        var text = args.Option("compression");
        if (text == null) return null;
        return Enum.TryParse<PakCompression>(text, ignoreCase: true, out var compression)
            ? [compression]
            : throw new ArgException($"Unknown --compression '{text}' (expects one of {string.Join(", ", Enum.GetNames<PakCompression>())}).");
    }

    public static UAsset Open(string path, ArgReader args)
    {
        if (!File.Exists(path)) throw new ArgException($"File not found: {path}");
        var version = ResolveVersion(args);
        var mappings = ResolveMappings(args);

        var game = ResolveGame(args);

        if (args.Flag("strict"))
        {
            var strictAsset = ResilientAssetLoader.OpenStrict(path, version, mappings, game);
            PrintOpenWarnings(path, ResilientAssetLoader.WarningsFor(strictAsset), args.Diagnostics);
            return strictAsset;
        }

        var asset = ResilientAssetLoader.Open(path, version, mappings, game, out var diagnostics);
        PrintOpenWarnings(path, diagnostics.Warnings, args.Diagnostics);
        if (diagnostics.ExportsSkipped)
        {
            // Without this the asset looks like it opened fine and simply has no properties,
            // which reads as "nothing to edit here" rather than "this failed to parse" - the
            // difference between moving on and passing --usmap/--version. Warnings go to
            // stderr so they never pollute piped output.
            args.Diagnostics.WriteLine(
                $"WARNING: {Path.GetFileName(path)} opened without property data - the structured parse failed, " +
                $"so every export will read as empty. Cause: {diagnostics.FullParseFailure?.GetType().Name}: " +
                $"{diagnostics.FullParseFailure?.Message} (re-run with --strict for the full stack; a missing or " +
                "wrong --usmap/--version is the usual reason).");
        }
        return asset;
    }

    // Warnings go to stderr so they never pollute piped output.
    private static void PrintOpenWarnings(string path, IReadOnlyList<string> warnings, TextWriter diagnostics)
    {
        foreach (var warning in warnings)
            diagnostics.WriteLine($"WARNING: {Path.GetFileName(path)}: {warning}");
    }

    /// <returns>What became of the backups (null when none was asked for); a backup that could not be placed does not fail the save.</returns>
    public static BackupResult? Save(UAsset asset, string path, bool backup)
    {
        // Backs up the .uexp too: a restore from the .uasset.bak alone would pair stale property bytes with the edited .uexp.
        if (backup) return PackageWriter.WriteWithBackup(asset, path, backupFolder: null);
        PackageWriter.Write(asset, path);
        return null;
    }

    /// <summary>Says on <paramref name="diagnostics"/> when a save's backup is not where it should be, and where the copy is.</summary>
    public static void WarnIfBackupMissing(BackupResult? backup, TextWriter diagnostics)
    {
        if (backup is not { Placed: false }) return;
        diagnostics.WriteLine(
            "Warning: the backup of the previous files could not be put in place" +
            (backup.Stranded.Count > 0 ? $"; the copies are at {string.Join(", ", backup.Stranded)}." : "; see the log."));
    }

    /// <summary>
    /// Saves to <paramref name="output"/>, in place when it is <paramref name="source"/> itself. A new path also gets
    /// the source's bulk-data companions (.ubulk, .uptnl), which Write() never touches but the package still needs.
    /// </summary>
    public static BackupResult? SaveAs(UAsset asset, string source, string output, bool backup)
    {
        var sameFile = string.Equals(Path.GetFullPath(output), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase);
        if (sameFile) return Save(asset, source, backup);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        PackageWriter.Write(asset, output);
        foreach (var extension in (string[])[".ubulk", ".uptnl"])
        {
            var companion = Path.ChangeExtension(source, extension);
            if (File.Exists(companion)) File.Copy(companion, Path.ChangeExtension(output, extension), overwrite: true);
        }
        return null;
    }

    /// <summary>Resolves an --export value as either a 0-based index or a (sub)string match against export names - whichever the caller finds more convenient to type.</summary>
    public static int ResolveExportIndex(UAsset asset, string value)
    {
        if (int.TryParse(value, out var index))
        {
            if (index < 0 || index >= asset.Exports.Count)
                throw new ArgException($"--export {index} is out of range (0..{asset.Exports.Count - 1}).");
            return index;
        }

        var matches = new List<int>();
        for (var i = 0; i < asset.Exports.Count; i++)
        {
            var name = FNameDisplay.ToDisplayString(asset.Exports[i].ObjectName);
            if (name.Contains(value, StringComparison.OrdinalIgnoreCase))
                matches.Add(i);
        }

        return matches.Count switch
        {
            0 => throw new ArgException($"No export name contains '{value}'."),
            1 => matches[0],
            _ => throw new ArgException(
                $"'{value}' matches {matches.Count} exports ({string.Join(", ", matches.Select(i => $"{i}:{FNameDisplay.ToDisplayString(asset.Exports[i].ObjectName)}"))}) - use an index instead."),
        };
    }
}
