using System.Text;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Editing;
using UAssetEditor.Core.Games;
using UAssetEditor.Core.PropertyAccess;
using UAssetEditor.Core.Search;
using UAssetEditor.Tooling;

namespace UAssetEditor.Cli;

/// <summary>
/// One method per verb, each opening whatever it needs fresh and printing plain, greppable
/// text - there's no server/session to keep alive between invocations, so every command is
/// self-contained and safe to call from a script or one at a time. The single-property
/// mutators (Set/Duplicate/Remove/AddNode) run their actual mutation through <see cref="ScriptOps"/>,
/// the same code <see cref="Script"/> uses to run several of them against one already-open asset
/// instead of paying a fresh open+parse per edit (see Script's own doc comment).
/// </summary>
internal static class Commands
{
    public static int Imports(ArgReader args)
    {
        var asset = AssetIo.Open(args.Positional(0, "file"), args);
        for (var i = 0; i < asset.Imports.Count; i++)
        {
            var import = asset.Imports[i];
            Console.WriteLine($"[{i}] {ImportPathResolver.GetFullPath(import, asset)} ({import.ClassName.Value?.Value})");
        }
        return 0;
    }

    public static int Exports(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        var members = args.Flag("members");

        for (var i = 0; i < asset.Exports.Count; i++)
        {
            var export = asset.Exports[i];
            var name = FNameDisplay.ToDisplayString(export.ObjectName);
            Console.WriteLine($"[{i}] {name} ({export.GetType().Name})");
            if (!members || export is not UAssetAPI.ExportTypes.StructExport structExport || structExport.LoadedProperties == null) continue;
            for (var m = 0; m < structExport.LoadedProperties.Length; m++)
            {
                var member = structExport.LoadedProperties[m];
                Console.WriteLine($"    {m}: {FNameDisplay.ToDisplayString(member.Name)} ({member.SerializedType?.Value?.Value})");
            }
        }

        return 0;
    }

    public static int Tree(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        var exportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var maxDepth = int.TryParse(args.Option("depth"), out var d) ? d : int.MaxValue;

        var root = args.Option("path") is { } startPath
            ? PropertyTreeExpander.GetChildren(
                PropertyLocator.Locate(asset, exportIndex, startPath)?.Property
                    ?? throw new ArgException($"No property at path '{startPath}'."),
                startPath, asset)
            : PropertyTreeExpander.GetExportRoot(asset.Exports[exportIndex], asset);

        PrintTree(root, asset, 0, maxDepth);
        return 0;
    }

    private static void PrintTree(IReadOnlyList<PropertyTreeItem> items, UAsset asset, int depth, int maxDepth)
    {
        foreach (var item in items)
        {
            Console.WriteLine($"{new string(' ', depth * 2)}{item.DisplayName}  [{item.Path}]");
            if (depth + 1 >= maxDepth) continue;
            PrintTree(PropertyTreeExpander.GetChildren(item.Property, item.Path, asset), asset, depth + 1, maxDepth);
        }
    }

    public static int Dump(ArgReader args)
    {
        var asset = AssetIo.Open(args.Positional(0, "file"), args);
        Console.Write(ScriptOps.ApplyDump(asset, args));
        return 0;
    }

    public static int Search(ArgReader args)
    {
        var path = args.Positional(0, "file-or-folder");

        var compare = args.Flag("regex") ? TextCompare.Regex : TextCompare.Contains;
        var query = new SearchQuery
        {
            ExportNameTerms = ToTerms(args.Option("export-name")),
            ExportNameCompare = compare,
            PropertyNameTerms = ToTerms(args.Option("property-name")),
            PropertyNameCompare = compare,
            ValueTerms = ToTerms(args.Option("value")),
            ValueCompare = compare,
            ReferenceTerms = ToTerms(args.Option("reference")),
            ReferenceCompare = compare,
            RegexTimeout = TimeSpan.FromSeconds(3),
        };

        SearchOutcome outcome;
        if (Directory.Exists(path))
        {
            var source = new LooseFolderAssetSource(path);
            var versions = AssetIo.ResolveVersions(args);
            outcome = SearchService.SearchAllAsync(source, versions, query).GetAwaiter().GetResult();
        }
        else
        {
            outcome = SearchService.SearchOne(AssetIo.Open(path, args), path, query);
        }

        foreach (var r in outcome.Results)
            Console.WriteLine($"{r.AssetPath} | [{r.ExportIndex}] {r.ExportName} | {r.PropertyPath} = {r.MatchedText}");

        foreach (var line in SearchSummary(outcome)) Console.WriteLine(line);
        foreach (var line in SearchWarnings(outcome)) args.Diagnostics.WriteLine(line);
        return 0;
    }

    /// <summary>The lines closing a search: the match count, then (only when some assets were not searched) how many and why.</summary>
    internal static IReadOnlyList<string> SearchSummary(SearchOutcome outcome)
    {
        var lines = new List<string> { $"-- {outcome.TotalMatches} match(es)" };
        if (outcome.AssetsSkipped == 0 && outcome.RegexTimeouts == 0) return lines;

        var parts = new List<string>();
        if (outcome.AssetsSkipped > 0) parts.Add($"{outcome.AssetsSkipped} of {outcome.AssetsSearched} asset(s) could not be read or searched");
        if (outcome.RegexTimeouts > 0) parts.Add($"{outcome.RegexTimeouts} hit the regular-expression time limit");
        lines.Add("-- not searched: " + string.Join(", ", parts));
        return lines;
    }

    /// <summary>One warning per asset that was not searched, with the reason (the first few only).</summary>
    internal static IReadOnlyList<string> SearchWarnings(SearchOutcome outcome) =>
        [.. outcome.Skips.Select(skip => $"Warning: {skip.AssetPath}: {skip.Reason}")];

    private static IReadOnlyList<ConditionTerm> ToTerms(string? text) =>
        text == null ? [] : [new ConditionTerm(text)];

    public static int Set(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        Console.WriteLine(ScriptOps.ApplySet(asset, args));
        MaybeSave(asset, path, args);
        return 0;
    }

    public static int Duplicate(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        Console.WriteLine(ScriptOps.ApplyDuplicate(asset, args));
        MaybeSave(asset, path, args);
        return 0;
    }

    public static int DuplicateRow(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        Console.WriteLine(ScriptOps.ApplyDuplicateRow(asset, args));
        MaybeSave(asset, path, args);
        return 0;
    }

    public static int SetString(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        Console.WriteLine(ScriptOps.ApplySetString(asset, args));
        MaybeSave(asset, path, args);
        return 0;
    }

    public static int Remove(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        Console.WriteLine(ScriptOps.ApplyRemove(asset, args));
        MaybeSave(asset, path, args);
        return 0;
    }

    public static int SpliceNode(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        Console.WriteLine(ScriptOps.ApplySpliceNode(asset, args));
        MaybeSave(asset, path, args);
        return 0;
    }

    public static int AddNode(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        Console.WriteLine(ScriptOps.ApplyAddNode(asset, args));
        MaybeSave(asset, path, args);
        return 0;
    }

    public static int AppendClone(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        Console.WriteLine(ScriptOps.ApplyAppendClone(asset, args));
        MaybeSave(asset, path, args);
        return 0;
    }

    public static int DuplicateExport(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        Console.WriteLine(ScriptOps.ApplyDuplicateExport(asset, args));
        MaybeSave(asset, path, args);
        return 0;
    }

    public static int RenameNames(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var output = args.Option("out") ?? path;
        var renames = ParseRenames(args.RequireOption("rename"));
        var asset = AssetIo.Open(path, args);

        IReadOnlyList<NameRenameResult> results;
        try
        {
            results = NameMapRenamer.Rename(asset, renames);
        }
        catch (ArgumentException ex)
        {
            throw new ArgException(ex.Message, ex);
        }

        foreach (var result in results)
            Console.WriteLine(result.Found ? $"{result.Rename.From} -> {result.Rename.To}" : $"{result.Rename.From}: not in the name map");
        if (results.Any(r => !r.Found)) throw new ArgException("Some names were not found; nothing saved.");

        if (!args.Flag("save")) return 0;
        AssetIo.WarnIfBackupMissing(AssetIo.SaveAs(asset, path, output, args.Flag("backup")), args.Diagnostics);
        Console.WriteLine($"Saved {output}");
        return 0;
    }

    public static int ToUnversioned(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var output = args.Option("out") ?? path;
        var schema = AssetIo.ResolveMappings(args) ?? throw new ArgException("--usmap is required (or --game with a stored usmap, see 'uacli usmap'): it is the game's schema to write for.");
        if (!File.Exists(path)) throw new ArgException($"File not found: {path}");
        // A tagged package is read without a schema: the schema describes the game, not the editor that cooked it.
        // An unversioned one can only be read with the schema it was written for.
        var source = AssetIo.ResolveMappings(args, "from-usmap");
        var asset = ResilientAssetLoader.OpenStrict(path, AssetIo.ResolveVersion(args), source, Game.None);

        IReadOnlyList<string> dropped;
        try
        {
            dropped = source == null ? UnversionedConverter.Convert(asset, schema) : UnversionedConverter.Reschema(asset, schema);
        }
        catch (ArgumentException ex)
        {
            throw new ArgException(ex.Message, ex);
        }

        foreach (var name in dropped) Console.WriteLine($"dropped {name} (not in the game's schema)");
        Console.WriteLine($"{path}: unversioned, {dropped.Count} propert{(dropped.Count == 1 ? "y" : "ies")} dropped");
        if (!args.Flag("save")) return 0;
        AssetIo.WarnIfBackupMissing(AssetIo.SaveAs(asset, path, output, args.Flag("backup")), args.Diagnostics);
        Console.WriteLine($"Saved {output}");
        return 0;
    }

    public static int StripVersions(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var output = args.Option("out") ?? path;
        if (!File.Exists(path)) throw new ArgException($"File not found: {path}");
        var asset = ResilientAssetLoader.OpenStrict(path, AssetIo.ResolveVersion(args), mappings: null, Game.None);
        try
        {
            VersionStripper.Strip(asset);
        }
        catch (ArgumentException ex)
        {
            throw new ArgException(ex.Message, ex);
        }

        Console.WriteLine($"{path}: summary unversioned, properties stay tagged");
        if (!args.Flag("save")) return 0;
        AssetIo.WarnIfBackupMissing(AssetIo.SaveAs(asset, path, output, args.Flag("backup")), args.Diagnostics);
        Console.WriteLine($"Saved {output}");
        return 0;
    }

    private static List<NameRename> ParseRenames(string text)
    {
        try
        {
            return [.. text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(NameRename.Parse)];
        }
        catch (FormatException ex)
        {
            throw new ArgException($"--rename: {ex.Message}", ex);
        }
    }

    public static int Script(ArgReader args) => ScriptRunner.Run(args);

    /// <summary>
    /// Runs a whole <see cref="RuleSet"/> (the same JSON shape the app's batch-edit tab
    /// saves/loads, since <see cref="EditRule"/> is already JSON-polymorphic) across one
    /// asset or every asset under a folder - full parity with the app's batch-edit tab, not
    /// just its single-property grid edits. Defaults to a dry-run preview; --apply actually
    /// writes.
    /// </summary>
    public static int Batch(ArgReader args)
    {
        var path = args.Positional(0, "file-or-folder");
        var rulesetPath = args.RequireOption("ruleset");
        if (!File.Exists(rulesetPath)) throw new ArgException($"Ruleset file not found: {rulesetPath}");

        var ruleSet = JsonSerializer.Deserialize<RuleSet>(File.ReadAllText(rulesetPath), AssetIo.RuleSetJsonOptions)
            ?? throw new ArgException($"'{rulesetPath}' did not contain a valid rule set.");

        var versions = AssetIo.ResolveVersions(args);
        IAssetSource source = Directory.Exists(path) ? new LooseFolderAssetSource(path) : new SingleFileAssetSource(path);

        var apply = args.Flag("apply");
        var changeSets = apply
            ? EditExecutor.ApplyAsync(source, versions, ruleSet, createBackup: args.Flag("backup"), backupFolder: null).GetAwaiter().GetResult()
            : EditExecutor.PreviewAsync(source, versions, ruleSet).GetAwaiter().GetResult();

        var totalChanges = 0;
        var changed = changeSets.Where(c => c.Changed).ToList();
        foreach (var changeSet in changed)
        {
            Console.WriteLine(changeSet.AssetPath);
            foreach (var change in changeSet.Changes)
            {
                Console.WriteLine($"  [{change.ExportIndex}] {change.ExportName} {change.PropertyPath} :: {change.RuleDescription}  {change.OldValue} -> {change.NewValue}");
                totalChanges++;
            }
        }

        var skipped = changeSets.Where(c => c.Skipped).ToList();
        foreach (var changeSet in skipped)
            Console.WriteLine($"SKIPPED {changeSet.AssetPath}: could not open: {changeSet.Error}");
        var failed = changeSets.Where(c => c.Failed).ToList();
        foreach (var changeSet in failed)
            Console.WriteLine($"FAILED {changeSet.AssetPath}: {changeSet.Error}");

        var skippedNote = skipped.Count > 0 ? $", {skipped.Count} skipped (unreadable)" : "";
        var failedNote = failed.Count > 0 ? $", {failed.Count} failed (not saved)" : "";
        Console.WriteLine($"-- {changed.Count} asset(s), {totalChanges} change(s){skippedNote}{failedNote}{(apply ? " (applied)" : " (preview only - pass --apply to write)")}");
        return failed.Count > 0 ? 1 : 0;
    }

    private static void MaybeSave(UAsset asset, string path, ArgReader args)
    {
        if (!args.Flag("save")) return;
        AssetIo.WarnIfBackupMissing(AssetIo.Save(asset, path, backup: args.Flag("backup")), args.Diagnostics);
        Console.WriteLine($"Saved {path}");
    }
}
