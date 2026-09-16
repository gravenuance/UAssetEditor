using System.Text;
using System.Text.Json;
using UAssetAPI;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Editing;
using UAssetEditor.Core.PropertyAccess;
using UAssetEditor.Core.Search;
using UAssetEditor.Core.Versioning;

namespace UAssetEditor.Cli;

/// <summary>
/// One method per verb, each opening whatever it needs fresh and printing plain, greppable
/// text - there's no server/session to keep alive between invocations, so every command is
/// self-contained and safe to call from a script or one at a time. The single-property
/// mutators (Set/Duplicate/Remove/AddNode) split their actual mutation out into an Apply*
/// helper so <see cref="Script"/> can run several of them against one already-open asset
/// instead of paying a fresh open+parse per edit (see Script's own doc comment).
/// </summary>
internal static class Commands
{
    public static int Exports(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);

        for (var i = 0; i < asset.Exports.Count; i++)
        {
            var export = asset.Exports[i];
            var name = export.ObjectName.Value?.Value ?? "";
            Console.WriteLine($"[{i}] {name} ({export.GetType().Name})");
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
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        var exportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var scope = args.Option("path");
        var filter = args.Option("filter");

        var results = scope != null
            ? SearchService.PropertiesUnder(asset, path, exportIndex, scope)
            : SearchService.PropertiesForExport(asset, path, exportIndex);

        foreach (var r in results)
        {
            if (filter != null && r.PropertyPath?.Contains(filter, StringComparison.OrdinalIgnoreCase) != true) continue;
            Console.WriteLine($"{r.PropertyPath} = {r.MatchedText}");
        }

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
        };

        IReadOnlyList<SearchResult> results;
        if (Directory.Exists(path))
        {
            var source = new LooseFolderAssetSource(path);
            var versions = new EngineVersionResolver { DefaultVersion = AssetIo.ResolveVersion(args), Mappings = AssetIo.ResolveMappings(args) };
            results = SearchService.SearchAllAsync(source, versions, query).GetAwaiter().GetResult();
        }
        else
        {
            var asset = AssetIo.Open(path, args);
            results = SearchService.SearchAsset(asset, path, query).ToList();
        }

        foreach (var r in results)
            Console.WriteLine($"{r.AssetPath} | [{r.ExportIndex}] {r.ExportName} | {r.PropertyPath} = {r.MatchedText}");

        Console.WriteLine($"-- {results.Count} match(es)");
        return 0;
    }

    private static IReadOnlyList<ConditionTerm> ToTerms(string? text) =>
        text == null ? [] : [new ConditionTerm(text)];

    public static int Set(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        Console.WriteLine(ApplySet(asset, args));
        MaybeSave(asset, path, args);
        return 0;
    }

    public static int Duplicate(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        Console.WriteLine(ApplyDuplicate(asset, args));
        MaybeSave(asset, path, args);
        return 0;
    }

    public static int Remove(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        Console.WriteLine(ApplyRemove(asset, args));
        MaybeSave(asset, path, args);
        return 0;
    }

    public static int AddNode(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        Console.WriteLine(ApplyAddNode(asset, args));
        MaybeSave(asset, path, args);
        return 0;
    }

    public static int AppendClone(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        Console.WriteLine(ApplyAppendClone(asset, args));
        MaybeSave(asset, path, args);
        return 0;
    }

    /// <summary>
    /// Runs a whole sequence of set/duplicate/remove/add-node ops against one asset opened
    /// once - the fix for the biggest cost of driving this CLI one verb at a time: each
    /// invocation re-opens and re-parses the whole file, and a real character's physics
    /// asset is big enough that reparsing it per edit dominates wall-clock time once a
    /// workflow needs more than one or two edits (e.g. splicing in a cloned KawaiiPhysics
    /// node needs an add-node, an AnimNodeData duplicate, and two LinkID sets - four
    /// separate opens without this). One op per line in --ops's file; blank lines and lines
    /// starting with '#' are skipped; a failing op is reported and skipped rather than
    /// aborting the rest, same tolerance <see cref="Editing.EditExecutor"/> already applies
    /// per-asset in a batch run.
    /// </summary>
    public static int Script(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var opsPath = args.RequireOption("ops");
        if (!File.Exists(opsPath)) throw new ArgException($"Ops file not found: {opsPath}");

        var asset = AssetIo.Open(path, args);

        var lineNumber = 0;
        foreach (var rawLine in File.ReadLines(opsPath))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var tokens = Tokenize(line);
            var opVerb = tokens[0];
            var opArgs = new ArgReader(tokens.Skip(1));

            try
            {
                var result = opVerb switch
                {
                    "set" => ApplySet(asset, opArgs),
                    "duplicate" => ApplyDuplicate(asset, opArgs),
                    "remove" => ApplyRemove(asset, opArgs),
                    "add-node" => ApplyAddNode(asset, opArgs),
                    "append-clone" => ApplyAppendClone(asset, opArgs),
                    _ => throw new ArgException($"Unknown op '{opVerb}' (expects set/duplicate/remove/add-node/append-clone)."),
                };
                Console.WriteLine($"[{lineNumber}] {opVerb}: {result}");
            }
            catch (ArgException ex)
            {
                Console.WriteLine($"[{lineNumber}] {opVerb}: SKIPPED - {ex.Message}");
            }
        }

        MaybeSave(asset, path, args);
        return 0;
    }

    /// <summary>Splits one script line into tokens, honoring "double-quoted segments" so a --value can contain spaces.</summary>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());

        return tokens;
    }

    private static string ApplySet(UAsset asset, ArgReader args)
    {
        var exportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var propertyPath = args.RequireOption("path");
        var newValue = args.RequireOption("value");

        var node = PropertyLocator.Locate(asset, exportIndex, propertyPath)
            ?? throw new ArgException($"No property at path '{propertyPath}'.");

        var oldValue = PropertyValueAccessor.AsSearchableString(node.Property, asset);
        if (!PropertyValueAccessor.TrySetStringValue(node.Property, newValue, asset))
            throw new ArgException($"'{propertyPath}' ({node.Property.GetType().Name}) isn't a settable scalar/string/name/text property.");
        PropertyValueAccessor.UpdateIsZeroFlag(node.Property);

        return $"{propertyPath}: {oldValue} -> {newValue}";
    }

    private static string ApplyDuplicate(UAsset asset, ArgReader args)
    {
        var exportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var elementPath = args.RequireOption("path");

        var (array, index) = PropertyLocator.LocateArrayElement(asset, exportIndex, elementPath)
            ?? throw new ArgException($"'{elementPath}' isn't an array element.");

        ArrayElementEditor.Duplicate(array, index);
        var newIndex = array.Value!.Length - 1;
        return $"Duplicated {elementPath} -> [{newIndex}]";
    }

    private static string ApplyRemove(UAsset asset, ArgReader args)
    {
        var exportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var elementPath = args.RequireOption("path");

        var (array, index) = PropertyLocator.LocateArrayElement(asset, exportIndex, elementPath)
            ?? throw new ArgException($"'{elementPath}' isn't an array element.");

        ArrayElementEditor.RemoveAt(array, index);
        return $"Removed {elementPath}";
    }

    private static string ApplyAddNode(UAsset asset, ArgReader args)
    {
        var cdoExportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var templateName = args.RequireOption("template");

        var (newName, _) = ClassPropertyDeclarer.DeclareClonedProperty(asset, cdoExportIndex, templateName);
        return $"Declared '{newName}' (cloned from '{templateName}')";
    }

    private static string ApplyAppendClone(UAsset asset, ArgReader args)
    {
        var exportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var sourcePath = args.RequireOption("from");
        var intoPath = args.Option("into");
        var sourceExportIndex = args.Option("from-export") is { } fromExport
            ? AssetIo.ResolveExportIndex(asset, fromExport)
            : exportIndex;

        var source = PropertyLocator.Locate(asset, sourceExportIndex, sourcePath)
            ?? throw new ArgException($"No property at path '{sourcePath}'.");

        if (intoPath == null)
        {
            var rootData = PropertyLocator.LocateExportData(asset, exportIndex)
                ?? throw new ArgException("Export has no top-level property list.");
            StructFieldEditor.AppendClone(rootData, source.Property);
            return $"Appended clone of {sourcePath} -> export root ({source.Property.Name})";
        }

        var target = PropertyLocator.Locate(asset, exportIndex, intoPath)?.Property;

        switch (target)
        {
            case UAssetAPI.PropertyTypes.Objects.ArrayPropertyData array:
                ArrayElementEditor.AppendClone(array, source.Property);
                return $"Appended clone of {sourcePath} -> {intoPath}[{array.Value!.Length - 1}]";

            case UAssetAPI.PropertyTypes.Structs.StructPropertyData @struct:
                StructFieldEditor.AppendClone(@struct, source.Property);
                return $"Appended clone of {sourcePath} -> {intoPath}.{source.Property.Name}";

            default:
                throw new ArgException($"'{intoPath}' isn't an array or struct property.");
        }
    }

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

        var versions = new EngineVersionResolver { DefaultVersion = AssetIo.ResolveVersion(args), Mappings = AssetIo.ResolveMappings(args) };
        IAssetSource source = Directory.Exists(path) ? new LooseFolderAssetSource(path) : new SingleFileAssetSource(path);

        var apply = args.Flag("apply");
        var changeSets = apply
            ? EditExecutor.ApplyAsync(source, versions, ruleSet, createBackup: args.Flag("backup"), backupFolder: null).GetAwaiter().GetResult()
            : EditExecutor.PreviewAsync(source, versions, ruleSet).GetAwaiter().GetResult();

        var totalChanges = 0;
        foreach (var changeSet in changeSets)
        {
            Console.WriteLine(changeSet.AssetPath);
            foreach (var change in changeSet.Changes)
            {
                Console.WriteLine($"  [{change.ExportIndex}] {change.ExportName} {change.PropertyPath} :: {change.RuleDescription}  {change.OldValue} -> {change.NewValue}");
                totalChanges++;
            }
        }

        Console.WriteLine($"-- {changeSets.Count} asset(s), {totalChanges} change(s){(apply ? " (applied)" : " (preview only - pass --apply to write)")}");
        return 0;
    }

    private static void MaybeSave(UAsset asset, string path, ArgReader args)
    {
        if (!args.Flag("save")) return;
        AssetIo.Save(asset, path, backup: args.Flag("backup"));
        Console.WriteLine($"Saved {path}");
    }
}
