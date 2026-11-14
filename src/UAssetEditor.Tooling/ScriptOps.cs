using System.Globalization;
using System.Text;
using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.PropertyAccess;
using UAssetEditor.Core.Search;

namespace UAssetEditor.Tooling;

/// <summary>One parsed line of an ops file.</summary>
internal sealed record ScriptOp(int Line, string Verb, ArgReader Args);

/// <summary>
/// The edit operations a script line can name, and the interpreter that runs a list of them against one open asset.
/// Each op is also what the single-property CLI verbs (<c>set</c>, <c>duplicate</c>, ...) run, so there is one
/// implementation of every edit. Nothing here reads or writes files: the caller opens and saves the asset.
/// </summary>
internal static class ScriptOps
{
    private static readonly Dictionary<string, Func<UAsset, ArgReader, string>> Ops = new(StringComparer.Ordinal)
    {
        ["set"] = ApplySet,
        ["duplicate"] = ApplyDuplicate,
        ["remove"] = ApplyRemove,
        ["add-node"] = ApplyAddNode,
        ["splice-node"] = ApplySpliceNode,
        ["append-clone"] = ApplyAppendClone,
        ["duplicate-export"] = ApplyDuplicateExport,
        ["duplicate-row"] = ApplyDuplicateRow,
        ["set-string"] = ApplySetString,
        ["dump"] = ApplyDump,
        ["bypass-node"] = ApplyBypassNode,
        ["check-graph"] = ApplyCheckGraph,
        ["list-nodes"] = ApplyListNodes,
        ["list-tags"] = ApplyListTags,
        ["set-tag"] = ApplySetTag,
        ["graft-nodes"] = ApplyGraftNodes,
    };

    public static IEnumerable<string> Verbs => Ops.Keys;

    /// <summary>
    /// Parses ops lines, skipping blank lines and <c>#</c> comments. Every line is checked before any asset is opened, so a
    /// typo stops the run before it touches anything. <paramref name="source"/> names where the lines came from, for errors.
    /// </summary>
    public static IReadOnlyList<ScriptOp> Parse(IEnumerable<string> lines, string source)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var ops = new List<ScriptOp>();
        var lineNumber = 0;
        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var tokens = Tokenize(line);
            if (!Ops.ContainsKey(tokens[0]))
                throw new ArgException($"{source}:{lineNumber}: unknown op '{tokens[0]}' (expects {string.Join('/', Ops.Keys)}).");
            try
            {
                ops.Add(new ScriptOp(lineNumber, tokens[0], new ArgReader(tokens.Skip(1))));
            }
            catch (ArgException ex)
            {
                throw new ArgException($"{source}:{lineNumber}: {ex.Message}", ex);
            }
        }
        return ops;
    }

    /// <summary>
    /// Runs <paramref name="ops"/> in order against <paramref name="asset"/>, one report line per op ("[line] verb: result"; a
    /// <c>dump</c> contributes its own text). A failing op is reported as SKIPPED and the rest still run. Ops that name no
    /// game, version or usmap of their own take them from <paramref name="openSettings"/>.
    /// </summary>
    public static string Run(UAsset asset, IReadOnlyList<ScriptOp> ops, ArgReader openSettings)
    {
        var output = new StringBuilder();
        foreach (var op in ops)
        {
            try
            {
                var result = Ops[op.Verb](asset, op.Args.WithOpenSettingsFrom(openSettings));
                if (op.Verb == "dump") output.Append(result);
                else output.AppendLine(CultureInfo.InvariantCulture, $"[{op.Line}] {op.Verb}: {result}");
            }
            catch (Exception ex) when (ex is ArgException or InvalidOperationException)
            {
                output.AppendLine(CultureInfo.InvariantCulture, $"[{op.Line}] {op.Verb}: SKIPPED - {ex.Message}");
            }
        }
        return output.ToString();
    }

    /// <summary>
    /// Splits an ops line on whitespace; double quotes group spaces and are dropped. <c>\"</c> is a literal quote
    /// (e.g. JSON values), except inside quotes right before whitespace or the line end, where it keeps the backslash and
    /// closes the quote, so <c>"D:\dir\"</c> works. Any other backslash is kept, so Windows paths need no escaping.
    /// </summary>
    internal static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length && line[i + 1] == '"' && !(inQuotes && EndsToken(line, i + 2)))
            {
                current.Append('"');
                i++;
                continue;
            }
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

    private static bool EndsToken(string line, int index) => index >= line.Length || char.IsWhiteSpace(line[index]);

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

    internal static string ApplyDuplicateRow(UAsset asset, ArgReader args)
    {
        var exportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var sourceRow = args.RequireOption("row");
        var newRow = args.RequireOption("as");
        try
        {
            DataTableRowDuplicator.Duplicate(asset, exportIndex, sourceRow, newRow);
        }
        catch (ArgumentException ex)
        {
            throw new ArgException(ex.Message, ex);
        }
        return $"Duplicated row {sourceRow} -> {newRow}";
    }

    internal static string ApplySetString(UAsset asset, ArgReader args)
    {
        var exportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var key = args.RequireOption("key");
        var value = args.RequireOption("value");
        string? previous;
        try
        {
            previous = StringTableEditor.Set(asset, exportIndex, key, value);
        }
        catch (ArgumentException ex)
        {
            throw new ArgException(ex.Message, ex);
        }
        return previous is null ? $"{key}: added = {value}" : $"{key}: {previous} -> {value}";
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
        return $"Graph consistent: {AnimGraphValidator.Validate(asset, cdoExportIndex)} nodes, each with a row and a handler, cached poses in place";
    }

    /// <summary>Read-only: the class's anim-graph nodes in node-index order, comma-separated, whatever they are called.</summary>
    internal static string ApplyListNodes(UAsset asset, ArgReader args) =>
        string.Join(",", AnimNodeSplicer.AnimNodeNames(asset, AssetIo.ResolveExportIndex(asset, args.RequireOption("export"))));

    /// <summary>Read-only: each node tag as Tag=Index(Node), comma-separated; the index counts from the end of the node list.</summary>
    internal static string ApplyListTags(UAsset asset, ArgReader args) =>
        string.Join(",", AnimNodeTags.List(asset, AssetIo.ResolveExportIndex(asset, args.RequireOption("export"))).Select(t => $"{t.Tag}={t.Index}({t.Node})"));

    internal static string ApplySetTag(UAsset asset, ArgReader args)
    {
        var cdoExportIndex = AssetIo.ResolveExportIndex(asset, args.RequireOption("export"));
        var tag = args.RequireOption("tag");
        var value = args.RequireOption("value");
        if (!int.TryParse(value, out var index)) throw new ArgException($"--value must be a node index, not '{value}'.");
        var set = AnimNodeTags.Set(asset, cdoExportIndex, tag, index);
        return $"{set.Tag} -> {set.Index} ({set.Node})";
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
}
