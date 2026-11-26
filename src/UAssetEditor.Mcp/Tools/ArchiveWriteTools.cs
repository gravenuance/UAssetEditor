using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UAssetAPI;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.AssetSources.IoStore;
using UAssetEditor.Core.Editing;
using UAssetEditor.Core.Games;
using UAssetEditor.Tooling;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Tools that write archives or loose files from .pak and IoStore containers. Every one refuses to replace an
/// existing output unless told to. Windows only: they run the bundled pak worker and retoc.</summary>
[McpServerToolType]
internal sealed partial class ArchiveWriteTools(MappingsStore store, ILogger<ArchiveWriteTools> logger)
{
    private const int DefaultUnpackFiles = 1000;
    private const int MaxUnpackFiles = 20_000;
    private const int MaxFailuresListed = 20;
    private const int MaxLogLines = 200;
    private static readonly LegacyNames LegacyNames = new("utoc", "output", "layer", "paks", "noPaksResolve", "overwrite=true");

    private static readonly string[] RivalsPackSwitches = ["obfuscate", "kawaiiPhysics", "patchDefaultHiddenMats", "defaultHiddenMaterialBitmaps"];

    [McpServerTool(Name = "unpack_pak", ReadOnly = false, Idempotent = true, Destructive = true, OpenWorld = false)]
    [Description("Extracts the entries of a .pak whose path contains filter into a folder, keeping their internal paths. The filter is required because a real game pak holds tens of GB and hundreds of thousands of entries: find the exact entry path with pak_list first. Refuses to extract more than maxFiles entries, and to overwrite an existing file unless overwrite is true. Progress is reported. Windows only." + Untrusted.Note)]
    public async Task<UnpackResult> UnpackPakAsync(
        [Description("Absolute path of the .pak.")] string pak,
        [Description("Absolute path of the folder to extract into; created if missing.")] string destination,
        [Description("Required. Only entries whose path contains this text (case-insensitive) are extracted.")] string filter,
        [Description("Replace files that already exist in the destination.")] bool overwrite = false,
        [Description("Refuse when more entries than this match, 1 to 20000 (default 1000): narrow the filter instead of raising it.")] int maxFiles = DefaultUnpackFiles,
        [Description(ToolParams.Game)] string? game = null,
        [Description(ToolParams.AesKey)] string? aesKey = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ToolRuntime.RequireWindows("unpack_pak");
        var path = ToolPaths.ExistingFile(pak, nameof(pak), ".pak");
        var contains = ToolInput.Required(filter, nameof(filter), 300);
        ToolInput.InRange(maxFiles, 1, MaxUnpackFiles, nameof(maxFiles));
        var folder = ToolPaths.Absolute(destination, nameof(destination));
        if (File.Exists(folder)) throw new McpException($"destination is a file: {folder}");
        var settings = OpenSettings.Create(store, game, null, null, aesKey: aesKey);
        var key = settings.AesKey;
        var variant = settings.PakVariant;
        var sink = new ProgressSink(progress, "Unpacked entries");

        return await ToolRuntime.UsePakWorkerBlockingAsync(() =>
        {
            // A threshold of zero keeps the source from extracting every asset up front: only the filtered entries are read.
            using var source = new PakAssetSource(path, key, largePakThresholdBytes: 0, variant: variant);
            bool Matches(string entry) => entry.Contains(contains, StringComparison.OrdinalIgnoreCase);

            var entries = source.ListAllEntries().Where(Matches).ToList();
            var matched = entries.Count;
            if (matched > maxFiles)
                throw new McpException($"filter matches {matched} entries, more than maxFiles ({maxFiles}). Narrow the filter; pak_list shows what it matches.");
            if (!overwrite) PakUnpacking.RefuseExisting(entries, folder, "overwrite=true");

            var result = PakUnpacker.Unpack(source, folder, Matches, sink, cancellationToken);
            var failed = result.FailedEntries
                .Take(MaxFailuresListed)
                .Select(f => Untrusted.Clean($"{f.Entry}: {f.Reason}", 400))
                .ToList();
            var note = matched == 0
                ? "No entry matched the filter; pak_list shows the entries."
                : result.HasFailures ? $"{result.FailedEntries.Count} entr(y/ies) could not be extracted." : "";
            return new UnpackResult(path, folder, matched, result.SucceededCount, failed, note);
        }, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "pack", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description("Builds a new archive from a loose folder: a legacy .pak when output ends in .pak, or an IoStore .utoc/.ucas pair through retoc when it ends in .utoc (which needs a UE4.25+ engine version). The source folder is never modified. Refuses to replace an existing output unless overwrite is true. For Marvel Rivals (game rivals), name the .utoc like retoc-rivals does (<mod>_9999999_P.utoc), since the name sets the container ID. Progress is reported. Windows only.")]
    public async Task<PackResult> PackAsync(
        [Description("Absolute path of the folder to pack.")] string sourceFolder,
        [Description("Absolute path of the .pak or .utoc to write; its folder is created if missing.")] string output,
        [Description(ToolParams.Overwrite)] bool overwrite = false,
        [Description(ToolParams.Game)] string? game = null,
        [Description(ToolParams.EngineVersion + " A .utoc needs a UE4.25+ one.")] string? engineVersion = null,
        [Description(ToolParams.Usmap + " Needed by kawaiiPhysics and the hidden-material patches.")] string? usmap = null,
        [Description(".pak only: mount point, default ../../../<folder name>/.")] string? mount = null,
        [Description(".pak only: pak version name, default V11.")] string? pakVersion = null,
        [Description(".pak only: Zlib, Gzip, Oodle, Zstd or LZ4; default none.")] string? compression = null,
        [Description(ToolParams.AesKey)] string? aesKey = null,
        [Description(".utoc, game rivals only: encrypt the container's data blocks, as retoc-rivals --obfuscate does.")] bool obfuscate = false,
        [Description(".utoc, game rivals only: port legacy KawaiiPhysics anim nodes to the game's Chains layout (needs a usmap).")] bool kawaiiPhysics = false,
        [Description(".utoc, game rivals only: fill LODInfo.DefaultHiddenMaterials from the mesh's carrier data (needs a usmap).")] bool patchDefaultHiddenMats = false,
        [Description(".utoc, game rivals only: instead set the hidden materials from masks, one per LOD, e.g. 0x5,0x1 (needs a usmap).")] string? defaultHiddenMaterialBitmaps = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ToolRuntime.RequireWindows("pack");
        var source = ToolPaths.ExistingFolder(sourceFolder, nameof(sourceFolder));
        var target = ToolPaths.NewFile(output, nameof(output), overwrite, ".pak", ".utoc");
        var isIoStore = string.Equals(Path.GetExtension(target), ".utoc", StringComparison.OrdinalIgnoreCase);

        var bitmaps = ToolInput.Optional(defaultHiddenMaterialBitmaps, nameof(defaultHiddenMaterialBitmaps), 200);
        var settings = OpenSettings.Create(store, game, engineVersion, usmap, aesKey: aesKey);
        PackOutputs.Refuse(source, target, settings.Game == Game.MarvelRivals, overwrite, pakIsStaged: true, ToolPaths.PackNames);
        var rivalsOnly = obfuscate || kawaiiPhysics || patchDefaultHiddenMats || bitmaps != null;

        return isIoStore
            ? await PackIoStoreAsync(source, target, settings, rivalsOnly, obfuscate, kawaiiPhysics, patchDefaultHiddenMats, bitmaps, progress, cancellationToken).ConfigureAwait(false)
            : await PackPakAsync(source, target, settings, mount, pakVersion, compression, rivalsOnly, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PackResult> PackPakAsync(
        string source, string target, OpenSettings settings, string? mount, string? pakVersion, string? compression,
        bool rivalsOnly, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        if (rivalsOnly)
            throw new McpException($"{string.Join(", ", RivalsPackSwitches)} only apply to IoStore (.utoc) output.");

        var mountPoint = ToolInput.Optional(mount, nameof(mount), 300) ?? PakPacker.DefaultMountPoint(source) ?? PakPacker.GenericMountPoint;
        var version = ToolInput.Optional(pakVersion, nameof(pakVersion), 40) is { } versionName
            ? ToolInput.Name<PakVersion>(versionName, nameof(pakVersion))
            : PakPacker.DefaultVersion;
        var codecs = ToolInput.Optional(compression, nameof(compression), 40) is { } codec
            ? new[] { ToolInput.Name<PakCompression>(codec, nameof(compression)) }
            : null;
        var sink = new ProgressSink(progress, "Packed files");
        var key = settings.AesKey;

        var succeeded = await StagedAsync(target, async staging =>
        {
            var result = await ToolRuntime.UsePakWorkerBlockingAsync(
                () => PakPacker.Build(source, staging, mountPoint, version, codecs, key, settings.PakVariant, sink, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (result.HasFailures) throw new McpException($"Packing failed: {Untrusted.Clean(result.FailedEntries[0].Reason, 400)}");
            return result.SucceededCount;
        }).ConfigureAwait(false);
        return new PackResult(target, "pak", succeeded, [$"mount point {mountPoint}, pak version {version}"], settings.Warnings());
    }

    /// <summary>Runs <paramref name="build"/> against a staging name beside <paramref name="target"/> and moves the file into
    /// place only once it returned, so a cancelled or failed build leaves no partial archive and keeps the file it was to replace.</summary>
    internal async Task<T> StagedAsync<T>(string target, Func<string, Task<T>> build)
    {
        var folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);
        var staging = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(target)}.{Guid.NewGuid():N}.partial{Path.GetExtension(target)}");
        try
        {
            var built = await build(staging).ConfigureAwait(false);
            File.Move(staging, target, overwrite: true);
            return built;
        }
        finally
        {
            DeleteStaging(staging);
        }
    }

    /// <summary>A no-op once the archive was moved into place. A staging file that cannot go (a scanner holding it) stays
    /// under its staging name, so the error the caller sees is the build's own, not the clean-up's.</summary>
    private void DeleteStaging(string staging)
    {
        try
        {
            File.Delete(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStagingNotDeleted(ex, staging);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the staged archive {Staging}; it stays under that name")]
    private partial void LogStagingNotDeleted(Exception exception, string staging);

    private static async Task<PackResult> PackIoStoreAsync(
        string source, string target, OpenSettings settings, bool rivalsOnly, bool obfuscate, bool kawaiiPhysics, bool patchDefaultHiddenMats,
        string? bitmaps, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        if (rivalsOnly && settings.Game != Game.MarvelRivals)
            throw new McpException($"{string.Join(", ", RivalsPackSwitches)} are only supported with game rivals.");

        var patches = RivalsPatches.Create(kawaiiPhysics, patchDefaultHiddenMats, bitmaps);
        if (patches != null && settings.UsmapPath == null)
            throw new McpException("The Marvel Rivals asset patches need a usmap (pass usmap, or store one with usmap_add) for packages that cannot be read without it.");

        var engineVersion = settings.EngineVersion;
        var retocVersion = EngineVersionMapping.ToRetocVersion(engineVersion)
            ?? throw new McpException($"'{engineVersion}' has no IoStore equivalent: pass engineVersion with a UE4.25+ version, or game.");

        var log = new LogLines(MaxLogLines);
        var warnings = new LogLines(MaxLogLines);
        var lines = 0;
        var gate = new Lock();
        // retoc's stdout and stderr call this from two threads; counting and reporting under one lock keeps progress rising.
        void Info(string line)
        {
            log.Add(line);
            lock (gate) progress?.Report(new ProgressNotificationValue { Progress = ++lines, Message = Untrusted.Clean(line, 200) });
        }

        var request = new IoStorePackRequest(source, target, retocVersion, settings.AesKey, new RetocZenOptions(settings.Game, obfuscate), patches, settings.UsmapPath);
        await IoStorePacker.PackAsync(request, Info, warnings.Add, cancellationToken).ConfigureAwait(false);

        return new PackResult(target, $"iostore ({retocVersion})", null, log.ToList(), [.. settings.Warnings(), .. warnings.ToList()]);
    }

    [McpServerTool(Name = "to_legacy", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description("Converts an IoStore .utoc to legacy format through retoc, into a loose folder or a .pak (when output ends in .pak): every entry, or only the comma-separated entry paths in filter (find them with iostore_list). Imports resolve through the enclosing Paks folder, or paks for a container outside it. Refuses to write into a non-empty folder or over an existing .pak unless overwrite is true. Windows only.")]
    public async Task<ConvertResult> ToLegacyAsync(
        [Description("Absolute path of the .utoc.")] string utoc,
        [Description("Absolute path of the folder, or of the .pak, to write.")] string output,
        [Description("Comma-separated entry paths to convert (as iostore_list prints them); omit to convert every entry.")] string? filter = null,
        [Description("modded (default) keeps the container's own edits; original gives the base game's vanilla content for them.")] string? layer = null,
        [Description("The game's Paks folder, for a container outside it.")] string? paks = null,
        [Description("Convert the container on its own, without resolving imports through a Paks folder.")] bool noPaksResolve = false,
        [Description(ToolParams.Overwrite)] bool overwrite = false,
        [Description(ToolParams.Game)] string? game = null,
        [Description(ToolParams.AesKey)] string? aesKey = null,
        CancellationToken cancellationToken = default)
    {
        ToolRuntime.RequireWindows("to_legacy");
        var container = ToolPaths.ExistingFile(utoc, nameof(utoc), ".utoc");
        var target = ToolPaths.Absolute(output, nameof(output));
        var filters = ToolInput.Optional(filter, nameof(filter), 20_000)
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        RetocLayer? retocLayer = layer == null ? null
            : ToolInput.OneOf(layer, nameof(layer), "modded", "original") == "modded" ? RetocLayer.Modded : RetocLayer.Original;
        var paksFolder = ToolInput.Optional(paks, nameof(paks)) is { } folder ? ToolPaths.ExistingFolder(folder, nameof(paks)) : null;
        var resolve = LegacyConversion.Resolve(noPaksResolve, retocLayer, paksFolder, LegacyNames);
        LegacyConversion.RefuseOutput(container, target, overwrite, LegacyNames);
        var settings = OpenSettings.Create(store, game, null, null, aesKey: aesKey);

        var warnings = new List<string>(settings.Warnings());
        var outcome = await LegacyConversion.RunAsync(container, target, filters, settings.AesKey, resolve, LegacyNames, warnings.Add, cancellationToken).ConfigureAwait(false);
        return new ConvertResult(target, [.. outcome.Lines(MaxLogLines).Select(l => Untrusted.Clean(l, 400))], [.. warnings.Select(w => Untrusted.Clean(w, 400))]);
    }

    [McpServerTool(Name = "repack", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description("Rebuilds a .pak into a new .pak, optionally applying a rule set's edits to its assets first. Never changes the source pak, and refuses to replace an existing output unless overwrite is true. If any asset's edit fails nothing is repacked; assets that cannot be opened are packed unchanged. The rebuild itself cannot be cancelled once started. Progress is reported while the rule set runs. Windows only.")]
    public async Task<RepackResult> RepackAsync(
        [Description("Absolute path of the source .pak.")] string pak,
        [Description("Absolute path of the new .pak to write.")] string output,
        [Description(RulesetInput.Description + " Omit to rebuild the pak unchanged.")] string? ruleset = null,
        [Description(ToolParams.Overwrite)] bool overwrite = false,
        [Description(ToolParams.Game)] string? game = null,
        [Description(ToolParams.EngineVersion)] string? engineVersion = null,
        [Description(ToolParams.Usmap)] string? usmap = null,
        [Description(ToolParams.AesKey)] string? aesKey = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ToolRuntime.RequireWindows("repack");
        var path = ToolPaths.ExistingFile(pak, nameof(pak), ".pak");
        var target = ToolPaths.NewFile(output, nameof(output), overwrite, ".pak");
        OutputPaths.RefuseSame(path, target, nameof(pak), nameof(output));
        var rules = ToolInput.Optional(ruleset, nameof(ruleset), RulesetInput.MaxCharacters) is { } text ? RulesetInput.Load(text) : null;
        var settings = OpenSettings.Create(store, game, engineVersion, usmap, aesKey: aesKey);
        var key = settings.AesKey;

        return await ToolRuntime.UsePakWorkerAsync(async () =>
        {
            using var source = await Task.Run(() => PakRepack.OpenSource(path, key, settings.PakVariant), cancellationToken).ConfigureAwait(false);
            var log = new LogLines(MaxLogLines);
            var outcome = await PakRepack.RunAsync(
                source, target, rules, settings.Args, note => log.Add($"{(note.Failed ? "Failed" : "Skipped")} {note.Detail}"), new ProgressSink(progress, "Edited assets"), cancellationToken).ConfigureAwait(false);
            return new RepackResult(path, target, outcome.Entries, outcome.AssetsEdited, outcome.Changes, log.ToList(), settings.Warnings());
        }, cancellationToken).ConfigureAwait(false);
    }
}
