using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UAssetEditor.Core.Games;
using UAssetEditor.Tooling;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Tools that run script ops against one asset: a dry run that never writes, and the edit that can.</summary>
[McpServerToolType]
internal sealed class AssetEditTools(MappingsStore store)
{
    private const int MaxOpsCharacters = 200_000;
    private const int MaxOpLines = 2000;
    private const int MaxOpTextCharacters = 4000;
    private const int MaxResultCharacters = 100_000;

    private const string OpsParameter =
        "Script ops, one per line, exactly as 'uacli script --ops' reads them: a verb then --option value pairs, " +
        "e.g. 'set --export 3 --path Chains[0].PhysicsSettings.Damping --value 0.5'. Double quotes group a value with spaces; " +
        "\\\" is a literal quote. Blank lines and lines starting with # are skipped. Every line is checked before the asset is opened; " +
        "a line that fails at run time is reported as skipped and the rest still run. A line cannot set --usmap, --version or --game " +
        "(use the tool's parameters), and its --from-file must be the absolute path of an existing local .uasset.";

    private const string OpsDescription =
        "Verbs: set, duplicate, remove, add-node, splice-node, append-clone, duplicate-export, duplicate-row, set-string, " +
        "dump, bypass-node, check-graph, list-nodes, list-tags, set-tag, graft-nodes. Several edits to one asset belong in one call: " +
        "the asset is opened once, and a duplicate appends at a predictable index that later lines can use. Prefer dump_export to read values.";

    [McpServerTool(Name = "preview_edit", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Dry run of script ops on an asset: runs them in memory and reports each old -> new value, and never writes anything. Use it before edit_asset. " + OpsDescription + Untrusted.Note)]
    public async Task<EditResultDto> PreviewEditAsync(
        [Description("Absolute path of the .uasset; its .uexp must sit beside it.")] string file,
        [Description(OpsParameter)] string ops,
        [Description(ToolParams.Game)] string? game = null,
        [Description(ToolParams.EngineVersion)] string? engineVersion = null,
        [Description(ToolParams.Usmap)] string? usmap = null,
        [Description(ToolParams.Strict)] bool strict = false,
        CancellationToken cancellationToken = default)
    {
        var settings = OpenSettings.Create(store, game, engineVersion, usmap, strict);
        return await RunAsync(file, ops, save: false, backup: false, settings, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "edit_asset", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = false)]
    [Description("Runs script ops on an asset. With save=false (the default) it is a dry run like preview_edit. With save=true it overwrites the .uasset and .uexp in place, after copying both to .bak files unless backup is false; nothing is saved when any op was skipped, so a half-applied edit never lands. " + OpsDescription + Untrusted.Note)]
    public async Task<EditResultDto> EditAssetAsync(
        [Description("Absolute path of the .uasset; its .uexp must sit beside it.")] string file,
        [Description(OpsParameter)] string ops,
        [Description("Write the result to disk. False runs the ops in memory and reports only.")] bool save = false,
        [Description("Copy the .uasset and .uexp to .bak files before overwriting them (default true). Only used with save.")] bool backup = true,
        [Description(ToolParams.Game)] string? game = null,
        [Description(ToolParams.EngineVersion)] string? engineVersion = null,
        [Description(ToolParams.Usmap)] string? usmap = null,
        [Description(ToolParams.Strict)] bool strict = false,
        CancellationToken cancellationToken = default)
    {
        var settings = OpenSettings.Create(store, game, engineVersion, usmap, strict);
        return await RunAsync(file, ops, save, backup, settings, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<EditResultDto> RunAsync(string? file, string? ops, bool save, bool backup, OpenSettings settings, CancellationToken cancellationToken)
    {
        var text = ToolInput.Required(ops, nameof(ops), MaxOpsCharacters);
        var lines = text.Split('\n');
        if (lines.Length > MaxOpLines) throw new McpException($"ops is limited to {MaxOpLines} lines; split the edit into several calls.");
        var parsed = ScriptOps.Parse(lines, "ops");
        if (parsed.Count == 0) throw new McpException("ops holds no op: every line is blank or a # comment.");
        ScriptOpPolicy.Validate(parsed, "ops");

        return await Task.Run(() =>
        {
            var (path, asset) = ToolAssets.Open(file, settings);
            var results = ScriptOps.Execute(asset, parsed, settings.Args);
            var skipped = results.Count(r => r.Skipped);

            var saved = false;
            string note;
            if (!save)
            {
                note = "Nothing was written: this was a dry run. Run edit_asset with save=true to write it.";
            }
            else if (skipped > 0)
            {
                note = $"Nothing was saved: {skipped} op(s) were skipped. Fix them and run again.";
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                AssetIo.Save(asset, path, backup);
                saved = true;
                note = backup ? $"Saved {path}; the previous files are kept as .bak beside it." : $"Saved {path} without a backup.";
            }

            var (opDtos, truncated) = ToDtos(results);
            return new EditResultDto(path, saved, saved && backup ? path + ".bak" : null, results.Count - skipped, skipped, opDtos, truncated, note, settings.Warnings());
        }, cancellationToken).ConfigureAwait(false);
    }

    private static (List<OpDto> Ops, bool Truncated) ToDtos(IReadOnlyList<OpResult> results)
    {
        var budget = MaxResultCharacters;
        var truncated = false;
        var dtos = new List<OpDto>(results.Count);
        foreach (var result in results)
        {
            var text = Untrusted.CleanLines(result.Text, MaxOpTextCharacters, 500);
            budget -= text.Length;
            if (budget < 0)
            {
                truncated = true;
                text = "(omitted: result size limit)";
            }

            dtos.Add(new OpDto(result.Op.Line, result.Op.Verb, result.Skipped, text));
        }
        return (dtos, truncated);
    }
}
