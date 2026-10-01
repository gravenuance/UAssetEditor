using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Games;

namespace UAssetEditor.Cli;

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

    private static GameProfile? ResolveProfile(ArgReader args)
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
    private static readonly ConcurrentDictionary<string, Lazy<Usmap>> LoadedMappings = new(StringComparer.OrdinalIgnoreCase);

    public static Usmap? ResolveMappings(ArgReader args)
    {
        var path = args.Option("usmap");
        if (path == null) return null;
        if (!File.Exists(path)) throw new ArgException($"--usmap file not found: {path}");
        return LoadedMappings.GetOrAdd(Path.GetFullPath(path), full => new Lazy<Usmap>(() => new Usmap(full))).Value.CreateAssetScope();
    }

    /// <summary>--aes if given, else the --game profile's key, else none.</summary>
    public static byte[]? ResolveAesKey(ArgReader args) => PakAesKey.Parse(GameProfile.ResolveAesKeyHex(ResolveProfile(args), args.Option("aes")));

    public static PakVersion ResolvePakVersion(ArgReader args)
    {
        var text = args.Option("pak-version") ?? "V11";
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
            : throw new ArgException($"Unknown --compression '{text}' (expects Zlib, Gzip, Oodle, or Zstd).");
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
            PrintOpenWarnings(path, ResilientAssetLoader.WarningsFor(strictAsset));
            return strictAsset;
        }

        var asset = ResilientAssetLoader.Open(path, version, mappings, game, out var diagnostics);
        PrintOpenWarnings(path, diagnostics.Warnings);
        if (diagnostics.ExportsSkipped)
        {
            // Without this the asset looks like it opened fine and simply has no properties,
            // which reads as "nothing to edit here" rather than "this failed to parse" - the
            // difference between moving on and passing --usmap/--version. Warnings go to
            // stderr so they never pollute piped output.
            Console.Error.WriteLine(
                $"WARNING: {Path.GetFileName(path)} opened without property data - the structured parse failed, " +
                $"so every export will read as empty. Cause: {diagnostics.FullParseFailure?.GetType().Name}: " +
                $"{diagnostics.FullParseFailure?.Message} (re-run with --strict for the full stack; a missing or " +
                "wrong --usmap/--version is the usual reason).");
        }
        return asset;
    }

    // Warnings go to stderr so they never pollute piped output.
    private static void PrintOpenWarnings(string path, IReadOnlyList<string> warnings)
    {
        foreach (var warning in warnings)
            Console.Error.WriteLine($"WARNING: {Path.GetFileName(path)}: {warning}");
    }

    public static void Save(UAsset asset, string path, bool backup)
    {
        // Backs up the .uexp too: a restore from the .uasset.bak alone would pair stale property bytes with the edited .uexp.
        if (backup) PackageWriter.WriteWithBackup(asset, path, backupFolder: null);
        else PackageWriter.Write(asset, path);
    }

    /// <summary>
    /// Saves to <paramref name="output"/>, in place when it is <paramref name="source"/> itself. A new path also gets
    /// the source's bulk-data companions (.ubulk, .uptnl), which Write() never touches but the package still needs.
    /// </summary>
    public static void SaveAs(UAsset asset, string source, string output, bool backup)
    {
        var sameFile = string.Equals(Path.GetFullPath(output), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase);
        if (sameFile)
        {
            Save(asset, source, backup);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        PackageWriter.Write(asset, output);
        foreach (var extension in (string[])[".ubulk", ".uptnl"])
        {
            var companion = Path.ChangeExtension(source, extension);
            if (File.Exists(companion)) File.Copy(companion, Path.ChangeExtension(output, extension), overwrite: true);
        }
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
            var name = asset.Exports[i].ObjectName.Value?.Value ?? "";
            if (name.Contains(value, StringComparison.OrdinalIgnoreCase))
                matches.Add(i);
        }

        return matches.Count switch
        {
            0 => throw new ArgException($"No export name contains '{value}'."),
            1 => matches[0],
            _ => throw new ArgException(
                $"'{value}' matches {matches.Count} exports ({string.Join(", ", matches.Select(i => $"{i}:{asset.Exports[i].ObjectName.Value?.Value}"))}) - use an index instead."),
        };
    }
}
