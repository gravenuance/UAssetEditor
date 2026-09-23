using System.Text;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
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
            var name = export.ObjectName.Value?.Value ?? "";
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
        Console.Write(ApplyDump(asset, args));
        return 0;
    }

    /// <summary>Read-only, so it also runs as a script op: a --plan run can dump many assets in one process.</summary>
    internal static string ApplyDump(UAsset asset, ArgReader args)
    {
        var exportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var scope = args.Option("path");
        var filter = args.Option("filter");

        var results = scope != null
            ? SearchService.PropertiesUnder(asset, asset.FilePath, exportIndex, scope)
            : SearchService.PropertiesForExport(asset, asset.FilePath, exportIndex);

        var output = new StringBuilder();
        foreach (var r in results)
        {
            if (filter != null && r.PropertyPath?.Contains(filter, StringComparison.OrdinalIgnoreCase) != true) continue;
            output.Append(r.PropertyPath).Append(" = ").AppendLine(r.MatchedText);
        }
        return output.ToString();
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

    public static int SpliceNode(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        Console.WriteLine(ApplySpliceNode(asset, args));
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

    public static int DuplicateExport(ArgReader args)
    {
        var path = args.Positional(0, "file");
        var asset = AssetIo.Open(path, args);
        Console.WriteLine(ApplyDuplicateExport(asset, args));
        MaybeSave(asset, path, args);
        return 0;
    }

    public static int Script(ArgReader args) => ScriptRunner.Run(args);

    internal static string ApplySet(UAsset asset, ArgReader args)
    {
        var exportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var propertyPath = args.RequireOption("path");
        var newValue = args.RequireOption("value");

        var existing = PropertyLocator.Locate(asset, exportIndex, propertyPath);
        var node = existing ?? PropertyLocator.LocateOrCreate(asset, exportIndex, propertyPath)
            ?? throw new ArgException($"No property at path '{propertyPath}'.");

        var oldValue = existing == null ? "(default)" : PropertyValueAccessor.AsSearchableString(node.Property, asset);
        if (!PropertyValueAccessor.TrySetStringValue(node.Property, newValue, asset))
        {
            if (existing == null) node.Owner?.RemoveAt(node.OwnerIndex);
            throw new ArgException($"'{propertyPath}' ({node.Property.GetType().Name}) can't take the value '{newValue}'.");
        }
        PropertyValueAccessor.UpdateIsZeroFlag(node.Property);

        return $"{propertyPath}: {oldValue} -> {newValue}";
    }

    internal static string ApplyDuplicate(UAsset asset, ArgReader args)
    {
        var exportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var elementPath = args.RequireOption("path");

        var (array, index) = PropertyLocator.LocateArrayElement(asset, exportIndex, elementPath)
            ?? throw new ArgException($"'{elementPath}' isn't an array element.");

        ArrayElementEditor.Duplicate(array, index);
        var newIndex = array.Value!.Length - 1;
        return $"Duplicated {elementPath} -> [{newIndex}]";
    }

    internal static string ApplyRemove(UAsset asset, ArgReader args)
    {
        var exportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var elementPath = args.RequireOption("path");

        var (array, index) = PropertyLocator.LocateArrayElement(asset, exportIndex, elementPath)
            ?? throw new ArgException($"'{elementPath}' isn't an array element.");

        ArrayElementEditor.RemoveAt(array, index);
        return $"Removed {elementPath}";
    }

    internal static string ApplyAddNode(UAsset asset, ArgReader args)
    {
        var cdoExportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var templateName = args.RequireOption("template");

        var (newName, _) = ClassPropertyDeclarer.DeclareClonedProperty(asset, cdoExportIndex, templateName);
        return $"Declared '{newName}' (cloned from '{templateName}')";
    }

    internal static string ApplySpliceNode(UAsset asset, ArgReader args)
    {
        var cdoExportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var node = AnimNodeSplicer.SpliceAfter(asset, cdoExportIndex, args.RequireOption("template"));
        return $"Spliced '{node.Name}' (node {node.NodeIndex}) after '{node.TemplateName}' (node {node.TemplateIndex}); '{node.ConsumerName}' now reads node {node.NodeIndex}";
    }

    internal static string ApplyBypassNode(UAsset asset, ArgReader args)
    {
        var cdoExportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var node = AnimNodeSplicer.Bypass(asset, cdoExportIndex, args.RequireOption("template"));
        return $"Bypassed '{node.Name}' (node {node.NodeIndex}); '{node.ReaderName}' now reads node {node.NowReads}";
    }

    internal static string ApplyCheckGraph(UAsset asset, ArgReader args)
    {
        var cdoExportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        return $"Graph consistent: {AnimGraphValidator.Validate(asset, cdoExportIndex)} nodes, each with a row and a handler";
    }

    internal static string ApplyGraftNodes(UAsset asset, ArgReader args)
    {
        var cdoExportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var donor = AssetIo.Open(args.RequireOption("from-file"), args);
        var donorCdoIndex = AssetIo.ResolveExportIndex(donor, args.Option("from-export") ?? args.RequireOption("export"));
        var nodes = args.RequireOption("nodes").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var grafted = AnimNodeGrafter.GraftBeforeRoot(asset, cdoExportIndex, donor, donorCdoIndex, nodes);
        return "Grafted before Root: " + string.Join(" -> ", grafted.Select(g => $"{g.DonorName} as '{g.Name}' (node {g.NodeIndex})"));
    }

    internal static string ApplyAppendClone(UAsset asset, ArgReader args)
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
    /// Deep-clones a whole export (e.g. a PhysicsAsset's SkeletalBodySetup/PhysicsConstraintTemplate
    /// body, which is its own top-level export, not an array element within one - see
    /// <see cref="ExportDuplicator"/>). Optionally also appends an object reference to the new
    /// export into an existing array-of-object-references property (--into-export/--into-path) -
    /// the array must already hold at least one element of the same reference type to clone from,
    /// matching how <see cref="ApplyAppendClone"/>'s array case works.
    /// </summary>
    internal static string ApplyDuplicateExport(UAsset asset, ArgReader args)
    {
        var sourceExportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var newIndex = ExportDuplicator.Duplicate(asset, sourceExportIndex);
        var result = $"Duplicated export [{sourceExportIndex}] -> [{newIndex}]";

        var intoExport = args.Option("into-export");
        var intoPath = args.Option("into-path");
        if (intoExport == null && intoPath == null)
            return result;
        if (intoExport == null || intoPath == null)
            throw new ArgException("--into-export and --into-path must be given together.");

        var containerExportIndex = AssetIo.ResolveExportIndex(asset, intoExport);
        var target = PropertyLocator.Locate(asset, containerExportIndex, intoPath)?.Property
            ?? throw new ArgException($"No property at path '{intoPath}'.");

        if (target is not ArrayPropertyData array)
            throw new ArgException($"'{intoPath}' isn't an array property.");
        if (array.Value is not { Length: > 0 } || array.Value[0] is not ObjectPropertyData)
            throw new ArgException($"'{intoPath}' has no existing object-reference element to use as a template.");

        var reference = (ObjectPropertyData)ArrayElementEditor.AppendClone(array, array.Value[0]);
        reference.Value = FPackageIndex.FromExport(newIndex);

        return $"{result}, appended reference -> {intoPath}[{array.Value.Length - 1}]";
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
