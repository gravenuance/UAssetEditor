using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Editing;
using UAssetEditor.Core.Games;
using UAssetEditor.Core.Versioning;
using UAssetEditor.Tooling;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Tools that run a rule set (the batch-edit definition the app saves) over one asset or a folder of them.</summary>
[McpServerToolType]
internal sealed class RulesetTools(MappingsStore store)
{
    private const int DefaultChanges = 200;
    private const int MaxChanges = 1000;
    private const int MaxProblemAssets = 50;

    [McpServerTool(Name = "preview_ruleset", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Dry run of a rule set over one asset or every .uasset under a folder: lists each property it would change as old -> new, and never writes anything. Use it before apply_ruleset. Progress is reported; the list is capped." + Untrusted.Note)]
    public async Task<RulesetResultDto> PreviewRulesetAsync(
        [Description("Absolute path of one .uasset, or of a folder searched recursively.")] string path,
        [Description(RulesetInput.Description)] string ruleset,
        [Description("Most changes to list, 1 to 1000 (default 200); the counts always cover all of them.")] int maxChanges = DefaultChanges,
        [Description(ToolParams.Game)] string? game = null,
        [Description(ToolParams.EngineVersion)] string? engineVersion = null,
        [Description(ToolParams.Usmap)] string? usmap = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = OpenSettings.Create(store, game, engineVersion, usmap);
        return await RunAsync(path, ruleset, apply: false, backup: false, maxChanges, settings, progress, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "apply_ruleset", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description("Runs a rule set over one asset or every .uasset under a folder and overwrites each changed asset in place (its .uasset and .uexp), unless backup is false, after copying both to .bak files (the state before this save, replaced on each save) and, on an asset's first save, to .orig.bak files (the original, never replaced). An asset that cannot be opened is skipped; one whose edit or save fails is left unsaved and reported. Run preview_ruleset first. Progress is reported and the list is capped. Cancelling stops between assets: those already saved stay saved." + Untrusted.Note)]
    public async Task<RulesetResultDto> ApplyRulesetAsync(
        [Description("Absolute path of one .uasset, or of a folder searched recursively.")] string path,
        [Description(RulesetInput.Description)] string ruleset,
        [Description("Copy each .uasset and .uexp to .bak files before overwriting them, and to .orig.bak files on the first save (default true).")] bool backup = true,
        [Description("Most changes to list, 1 to 1000 (default 200); the counts always cover all of them.")] int maxChanges = DefaultChanges,
        [Description(ToolParams.Game)] string? game = null,
        [Description(ToolParams.EngineVersion)] string? engineVersion = null,
        [Description(ToolParams.Usmap)] string? usmap = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var settings = OpenSettings.Create(store, game, engineVersion, usmap);
        return await RunAsync(path, ruleset, apply: true, backup, maxChanges, settings, progress, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RulesetResultDto> RunAsync(
        string path, string ruleset, bool apply, bool backup, int maxChanges, OpenSettings settings,
        IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        ToolInput.InRange(maxChanges, 1, MaxChanges, nameof(maxChanges));
        var (target, isFolder) = ToolPaths.ExistingFileOrFolder(path, nameof(path), ".uasset");
        var rules = RulesetInput.Load(ruleset);

        var versions = await Task.Run(
            () => new EngineVersionResolver { DefaultVersion = settings.EngineVersion, Mappings = AssetIo.ResolveMappings(settings.Args), Game = settings.Game },
            cancellationToken).ConfigureAwait(false);
        IAssetSource source = isFolder ? new LooseFolderAssetSource(target) : new SingleFileAssetSource(target);
        var assetFiles = isFolder ? Directory.EnumerateFiles(target, "*.uasset", SearchOption.AllDirectories) : [target];

        var sink = new ProgressSink(progress, apply ? "Edited assets" : "Checked assets");
        var editProgress = new InlineProgress<EditProgress>(p => sink.Report((p.Completed, p.Total)));

        // Every asset it may open, so an edit_asset on one of them waits for the run (and the run for it).
        var results = await PathLocks.RunAsync(assetFiles, async () => apply
            ? await EditExecutor.ApplyAsync(source, versions, rules, backup, backupFolder: null, editProgress, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await EditExecutor.PreviewAsync(source, versions, rules, editProgress, cancellationToken: cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        return ToResult(target, apply, results, maxChanges, settings);
    }

    private static RulesetResultDto ToResult(string target, bool apply, IReadOnlyList<AssetChangeSet> results, int maxChanges, OpenSettings settings)
    {
        var ordered = results.OrderBy(r => r.AssetPath, StringComparer.OrdinalIgnoreCase).ToList();
        var changed = ordered.Where(r => r.Changed && r.Changes.Count > 0).ToList();
        var skipped = ordered.Where(r => r.Skipped).ToList();
        var failed = ordered.Where(r => r.Failed).ToList();

        var assets = new List<AssetChangesDto>();
        var remaining = maxChanges;
        var truncated = false;
        foreach (var set in changed)
        {
            if (remaining <= 0)
            {
                truncated = true;
                break;
            }

            var shown = set.Changes.Take(remaining).Select(ToChange).ToList();
            truncated |= shown.Count < set.Changes.Count;
            remaining -= shown.Count;
            assets.Add(new AssetChangesDto(Untrusted.Clean(set.AssetPath, 500), apply ? "Saved" : "WouldChange", null, shown));
        }

        foreach (var set in failed.Concat(skipped).Take(MaxProblemAssets))
        {
            assets.Add(new AssetChangesDto(
                Untrusted.Clean(set.AssetPath, 500), set.Skipped ? "Skipped" : "Failed", Untrusted.Clean(set.Error, 500), []));
        }

        var note = apply
            ? failed.Count > 0
                ? $"{failed.Count} asset(s) failed and were not saved; the others were."
                : "Changed assets were saved in place."
            : "Nothing was written: this was a dry run. Run apply_ruleset to write it.";
        return new RulesetResultDto(
            target, apply, changed.Count, changed.Sum(r => r.Changes.Count), skipped.Count, failed.Count, assets, truncated, note, settings.Warnings());
    }

    private static ChangeDto ToChange(PropertyChange change) => new(
        change.ExportIndex,
        Untrusted.Clean(change.ExportName),
        change.PropertyPath is { } path ? Untrusted.Clean(path, 500) : null,
        Untrusted.Clean(change.RuleDescription),
        Untrusted.Clean(change.OldValue, 500),
        Untrusted.Clean(change.NewValue, 500));
}
