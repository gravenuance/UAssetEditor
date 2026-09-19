using System.Text.Json;
using UAssetAPI;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.AssetSources.IoStore;
using UAssetEditor.Core.Editing;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.Cli;

/// <summary>
/// Pak/IoStore archive operations - unpack, pack, repack, and legacy&lt;-&gt;IoStore conversion
/// via retoc - mirroring the app's own Unpack/Pack/Convert dialogs (see
/// <c>UnpackPakViewModel</c>, <c>PackFolderViewModel</c>, <c>ConvertIoStoreToLegacyViewModel</c>,
/// and <c>MainViewModel</c>'s RepackCommand) instead of just the property-editing commands in
/// <see cref="Commands"/>. Every command here that opens a .pak spawns the shared out-of-process
/// pak worker (<see cref="PakWorkerProcess.Shared"/>) - <see cref="Program"/> disposes it once
/// at the very end of Main, regardless of which command ran, so this exe never leaves an
/// orphaned worker process behind the way a missed Dispose would.
/// </summary>
internal static class PakCommands
{
    public static int PakInfo(ArgReader args)
    {
        var pakPath = args.Positional(0, "pak");
        if (!File.Exists(pakPath)) throw new ArgException($"File not found: {pakPath}");

        var info = PakMountPointReader.Read(pakPath, AssetIo.ResolveAesKey(args));
        Console.WriteLine($"MountPoint: {info.MountPoint}");
        Console.WriteLine($"Version: {info.Version}");
        return 0;
    }

    public static int PakList(ArgReader args)
    {
        var pakPath = args.Positional(0, "pak");
        if (!File.Exists(pakPath)) throw new ArgException($"File not found: {pakPath}");
        var filter = args.Option("filter");

        using var source = new PakAssetSource(pakPath, AssetIo.ResolveAesKey(args));
        var uassetEntries = new HashSet<string>(source.EnumerateAssetPaths(), StringComparer.OrdinalIgnoreCase);

        var count = 0;
        foreach (var entry in source.ListAllEntries().OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
        {
            if (filter != null && !entry.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine(uassetEntries.Contains(entry) ? $"* {entry}" : $"  {entry}");
            count++;
        }

        Console.WriteLine($"-- {count} entr(y/ies) ('*' = .uasset)");
        return 0;
    }

    public static int Unpack(ArgReader args)
    {
        var pakPath = args.Positional(0, "pak");
        var destination = args.Positional(1, "destination folder");
        if (!File.Exists(pakPath)) throw new ArgException($"File not found: {pakPath}");

        // Without a filter this walks every entry, which on a real game pak (Days Gone's is
        // 30GB / 216k entries) means a multi-minute run and a full-disk extraction when all
        // the caller wanted was one asset.
        var filter = args.Option("filter");
        using var source = new PakAssetSource(pakPath, AssetIo.ResolveAesKey(args));
        var result = PakUnpacker.Unpack(
            source,
            destination,
            filter == null ? null : e => e.Contains(filter, StringComparison.OrdinalIgnoreCase));

        foreach (var (entry, reason) in result.FailedEntries)
            Console.WriteLine($"FAILED {entry}: {reason}");
        Console.WriteLine($"-- unpacked {result.SucceededCount} file(s) to {destination}{(result.HasFailures ? $" ({result.FailedEntries.Count} failed)" : "")}");
        return 0;
    }

    /// <summary>Builds a new archive from a loose folder - a legacy .pak via <see cref="PakPacker"/> if --output ends in ".pak", or an IoStore .utoc/.ucas pair via retoc's to-zen if it ends in ".utoc".</summary>
    public static int Pack(ArgReader args)
    {
        var sourceFolder = args.Positional(0, "source folder");
        var output = args.Positional(1, "output path");
        if (!Directory.Exists(sourceFolder)) throw new ArgException($"Folder not found: {sourceFolder}");

        return Path.GetExtension(output).ToUpperInvariant() switch
        {
            ".UTOC" => PackToIoStore(args, sourceFolder, output),
            ".PAK" => PackToPak(args, sourceFolder, output),
            _ => throw new ArgException($"--output must end in .pak or .utoc, not '{Path.GetExtension(output)}'."),
        };
    }

    private static int PackToPak(ArgReader args, string sourceFolder, string output)
    {
        var mountPoint = args.Option("mount") ?? $"../../../{new DirectoryInfo(sourceFolder.TrimEnd('\\', '/')).Name}/";
        var result = PakPacker.Build(sourceFolder, output, mountPoint, AssetIo.ResolvePakVersion(args), AssetIo.ResolveCompression(args), AssetIo.ResolveAesKey(args));

        if (result.HasFailures)
        {
            Console.WriteLine($"FAILED: {result.FailedEntries[0].Reason}");
            return 1;
        }
        Console.WriteLine($"-- packed {result.SucceededCount} file(s) to {output} (mount point {mountPoint})");
        return 0;
    }

    private static int PackToIoStore(ArgReader args, string sourceFolder, string output)
    {
        var engineVersion = AssetIo.ResolveVersion(args);
        var retocVersion = EngineVersionMapping.ToRetocVersion(engineVersion)
            ?? throw new ArgException($"'{engineVersion}' has no IoStore equivalent - pass --version with a UE4.25+ engine version.");

        var retocInput = RetocDirectoryInputResolver.Resolve(sourceFolder)
            ?? throw new ArgException("Source folder is a drive root - pick a folder that isn't.");

        RetocProcess.ConvertToZenAsync(retocInput, output, retocVersion, AssetIo.ResolveAesKey(args)).GetAwaiter().GetResult();
        Console.WriteLine($"-- packed to {output} ({retocVersion})");
        return 0;
    }

    /// <summary>Lists every chunk path in a .utoc container via retoc's own `list` - the IoStore counterpart to <see cref="PakList"/>, and how to find the exact entry path <see cref="ToLegacy"/>'s --filter expects out of a large shared container.</summary>
    public static int IoStoreList(ArgReader args)
    {
        var utocPath = args.Positional(0, "utoc");
        if (!File.Exists(utocPath)) throw new ArgException($"File not found: {utocPath}");
        var filter = args.Option("filter");

        var entries = RetocProcess.ListAsync(utocPath, AssetIo.ResolveAesKey(args)).GetAwaiter().GetResult();
        var count = 0;
        foreach (var entry in entries.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
        {
            if (filter != null && !entry.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine(entry);
            count++;
        }

        Console.WriteLine($"-- {count} entr(y/ies)");
        return 0;
    }

    /// <summary>
    /// Converts entries of an IoStore container to legacy format via retoc's to-legacy - every
    /// entry by default, or only the comma-separated paths passed via --filter (e.g. to pull one
    /// specific asset back out of a large shared chunk like pakchunkCharacter-Windows.utoc without
    /// converting the whole thing). Mirrors ConvertIoStoreToLegacyViewModel's own auto-resolution:
    /// a standalone .utoc that only overrides a handful of assets often can't resolve imports into
    /// whatever container actually owns the rest on its own, so when this file sits under a real
    /// Paks folder, retoc is pointed at a merged view of that whole folder instead (still scoped
    /// to just the requested entries), which lets it resolve imports the single file alone
    /// couldn't - see <see cref="RetocLayerResolver"/> for why a merged view, not the Paks folder
    /// itself, is required for --layer modded (the default) to actually apply the file's own edits
    /// rather than silently falling back to the base game's vanilla content for its own entries.
    /// </summary>
    public static int ToLegacy(ArgReader args)
    {
        var utocPath = args.Positional(0, "utoc");
        var output = args.Positional(1, "output");
        if (!File.Exists(utocPath)) throw new ArgException($"File not found: {utocPath}");

        var aesKey = AssetIo.ResolveAesKey(args);
        var input = utocPath;
        IReadOnlyList<string> filters = args.Option("filter")
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        RetocInputScope? scope = null;
        if (!args.Flag("no-paks-resolve"))
        {
            var layer = ResolveLayer(args);
            scope = RetocLayerResolver.Resolve(utocPath, layer);
            if (scope.InputDirectory != utocPath)
            {
                if (filters.Count == 0)
                {
                    var ownEntries = RetocProcess.ListAsync(utocPath, aesKey).GetAwaiter().GetResult();
                    if (ownEntries.Count > 0)
                    {
                        filters = ownEntries;
                        Console.WriteLine($"-- resolving imports via enclosing Paks folder ({layer} layer, {ownEntries.Count} entr(y/ies) of '{Path.GetFileName(utocPath)}')");
                    }
                }
                input = scope.InputDirectory;
            }
        }

        try
        {
            RetocProcess.ConvertToLegacyAsync(input, output, filters, aesKey).GetAwaiter().GetResult();
        }
        finally
        {
            scope?.Dispose();
        }

        Console.WriteLine($"-- converted to {output}");
        return 0;
    }

    private static RetocLayer ResolveLayer(ArgReader args) => args.Option("layer") switch
    {
        null => RetocLayer.Modded,
        "modded" => RetocLayer.Modded,
        "original" => RetocLayer.Original,
        var other => throw new ArgException($"Unknown --layer '{other}' (expects 'original' or 'modded')."),
    };

    /// <summary>
    /// Rebuilds a .pak, optionally applying a RuleSet's edits first (same JSON shape as
    /// <see cref="Commands.Batch"/>) - the pak counterpart to it, since editing an entry
    /// inside a pak means extract-edit-repack rather than editing a loose file in place.
    /// Always writes to a new file; never overwrites the source pak (matches
    /// <see cref="PakRepacker"/>'s own contract).
    /// </summary>
    public static int Repack(ArgReader args)
    {
        var pakPath = args.Positional(0, "pak");
        var output = args.Positional(1, "output pak");
        if (!File.Exists(pakPath)) throw new ArgException($"File not found: {pakPath}");

        var aesKey = AssetIo.ResolveAesKey(args);
        using var source = new PakAssetSource(pakPath, aesKey);

        var rulesetPath = args.Option("ruleset");
        if (rulesetPath != null)
        {
            if (!File.Exists(rulesetPath)) throw new ArgException($"Ruleset file not found: {rulesetPath}");
            var ruleSet = JsonSerializer.Deserialize<RuleSet>(File.ReadAllText(rulesetPath), AssetIo.RuleSetJsonOptions)
                ?? throw new ArgException($"'{rulesetPath}' did not contain a valid rule set.");
            var versions = new EngineVersionResolver { DefaultVersion = AssetIo.ResolveVersion(args), Mappings = AssetIo.ResolveMappings(args) };

            var changeSets = EditExecutor.ApplyAsync(source, versions, ruleSet, createBackup: false, backupFolder: null).GetAwaiter().GetResult();
            var totalChanges = changeSets.Sum(c => c.Changes.Count);
            Console.WriteLine($"-- applied ruleset: {changeSets.Count} asset(s), {totalChanges} change(s)");
        }

        var result = PakRepacker.Build(source, output, aesKey: aesKey);
        if (result.HasFailures)
        {
            Console.WriteLine($"FAILED: {result.FailedEntries[0].Reason}");
            return 1;
        }
        Console.WriteLine($"-- repacked {result.SucceededCount} entr(y/ies) to {output}");
        return 0;
    }
}
