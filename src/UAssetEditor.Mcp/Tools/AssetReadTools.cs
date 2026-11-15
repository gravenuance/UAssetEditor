using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UAssetAPI;
using UAssetAPI.FieldTypes;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Games;
using UAssetEditor.Core.PropertyAccess;
using UAssetEditor.Core.Search;
using UAssetEditor.Core.Versioning;
using UAssetEditor.Tooling;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Tools that read one asset or search many. None writes anything.</summary>
[McpServerToolType]
internal sealed class AssetReadTools(MappingsStore store)
{
    private const int MaxExports = 2000;
    private const int MaxMembersPerExport = 200;
    private const int MaxImports = 3000;
    private const int MaxTreeNodes = 1000;
    private const int MaxDepth = 10;
    private const int DefaultResults = 200;
    private const int MaxResults = 2000;
    private const int MaxSearchHits = 1000;
    private const int MaxValueLength = 500;
    private static readonly TimeSpan SearchRegexTimeout = TimeSpan.FromSeconds(3);
    private const int MaxDumpCharacters = 150_000;
    private const int MaxMemberCharacters = 150_000;

    [McpServerTool(Name = "list_exports", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Lists an asset's exports: index, name and export type, and optionally each class or struct export's declared members (the first 200 of each, within a shared 150,000-character budget; truncated says when a list was cut). The index (or a unique part of the name) is what the other asset tools take as export." + Untrusted.Note)]
    public async Task<ExportsResult> ListExportsAsync(
        [Description("Absolute path of the .uasset or .umap; its .uexp must sit beside it.")] string file,
        [Description("Also list the declared members of class and struct exports.")] bool members = false,
        [Description(ToolParams.Game)] string? game = null,
        [Description(ToolParams.EngineVersion)] string? engineVersion = null,
        [Description(ToolParams.Usmap)] string? usmap = null,
        [Description(ToolParams.Strict)] bool strict = false,
        CancellationToken cancellationToken = default)
    {
        var settings = OpenSettings.Create(store, game, engineVersion, usmap, strict);
        return await Task.Run(() =>
        {
            var (path, asset) = ToolAssets.Open(file, settings);
            return DescribeExports(asset, path, members, settings.Warnings());
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static ExportsResult DescribeExports(UAsset asset, string path, bool members, IReadOnlyList<string> warnings)
    {
        var shown = new List<ExportDto>(Math.Min(asset.Exports.Count, MaxExports));
        var budget = MaxMemberCharacters;
        var classesOverTheCount = 0;
        var cutByBudget = false;
        for (var i = 0; i < asset.Exports.Count && i < MaxExports; i++)
        {
            var export = asset.Exports[i];
            List<MemberDto>? listed = null;
            if (members && export is UAssetAPI.ExportTypes.StructExport { LoadedProperties: { } properties })
            {
                listed = MembersOf(properties, ref budget, out var overTheCount, out var overTheBudget);
                if (overTheCount) classesOverTheCount++;
                cutByBudget |= overTheBudget;
            }

            shown.Add(new ExportDto(i, Untrusted.Clean(FNameDisplay.ToDisplayString(export.ObjectName)), export.GetType().Name, listed));
        }

        var notes = new List<string>(warnings);
        if (cutByBudget)
            notes.Add($"Member lists were cut after {MaxMemberCharacters} characters of member names and types; list_exports with members=false, then dump_export or get_tree for the export you need.");
        if (classesOverTheCount > 0)
            notes.Add($"{classesOverTheCount} class or struct export(s) declare more than {MaxMembersPerExport} members; only the first {MaxMembersPerExport} of each are listed.");
        return new ExportsResult(path, asset.Exports.Count, shown, asset.Exports.Count > shown.Count || cutByBudget || classesOverTheCount > 0, notes);
    }

    /// <summary>The first members of one export, at most <see cref="MaxMembersPerExport"/> and while the shared character budget lasts.</summary>
    private static List<MemberDto> MembersOf(FProperty[] properties, ref int budget, out bool overTheCount, out bool overTheBudget)
    {
        overTheCount = properties.Length > MaxMembersPerExport;
        overTheBudget = false;

        var listed = new List<MemberDto>(Math.Min(properties.Length, MaxMembersPerExport));
        foreach (var (member, index) in properties.Take(MaxMembersPerExport).Select((m, i) => (m, i)))
        {
            var name = Untrusted.Clean(FNameDisplay.ToDisplayString(member.Name));
            var type = member.SerializedType?.Value?.Value is { } text ? Untrusted.Clean(text) : null;
            var cost = name.Length + (type?.Length ?? 0);
            if (budget < cost)
            {
                overTheBudget = true;
                break;
            }

            budget -= cost;
            listed.Add(new MemberDto(index, name, type));
        }
        return listed;
    }

    [McpServerTool(Name = "list_imports", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Lists an asset's imports: index, full object path and class." + Untrusted.Note)]
    public async Task<ImportsResult> ListImportsAsync(
        [Description("Absolute path of the .uasset or .umap; its .uexp must sit beside it.")] string file,
        [Description(ToolParams.Game)] string? game = null,
        [Description(ToolParams.EngineVersion)] string? engineVersion = null,
        [Description(ToolParams.Usmap)] string? usmap = null,
        [Description(ToolParams.Strict)] bool strict = false,
        CancellationToken cancellationToken = default)
    {
        var settings = OpenSettings.Create(store, game, engineVersion, usmap, strict);
        return await Task.Run(() =>
        {
            var (path, asset) = ToolAssets.Open(file, settings);
            var shown = new List<ImportDto>(Math.Min(asset.Imports.Count, MaxImports));
            for (var i = 0; i < asset.Imports.Count && i < MaxImports; i++)
            {
                var import = asset.Imports[i];
                shown.Add(new ImportDto(
                    i,
                    Untrusted.Clean(ImportPathResolver.GetFullPath(import, asset)),
                    import.ClassName.Value?.Value is { } className ? Untrusted.Clean(className) : null));
            }

            return new ImportsResult(path, asset.Imports.Count, shown, asset.Imports.Count > shown.Count, settings.Warnings());
        }, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "get_tree", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Shows the struct, array and map nodes under an export, or under one property, to the given depth, each with the path the other tools take. Scalar leaves are not shown: use dump_export for values." + Untrusted.Note)]
    public async Task<TreeResult> GetTreeAsync(
        [Description("Absolute path of the .uasset or .umap; its .uexp must sit beside it.")] string file,
        [Description("Export index, or a unique part of its name.")] string export,
        [Description("Property path to start from, like Chains[0].PhysicsSettings. Omit for the export's top level.")] string? path = null,
        [Description("How many levels to show, 1 to 10 (default 2).")] int depth = 2,
        [Description(ToolParams.Game)] string? game = null,
        [Description(ToolParams.EngineVersion)] string? engineVersion = null,
        [Description(ToolParams.Usmap)] string? usmap = null,
        [Description(ToolParams.Strict)] bool strict = false,
        CancellationToken cancellationToken = default)
    {
        ToolInput.InRange(depth, 1, MaxDepth, nameof(depth));
        var startPath = ToolInput.Optional(path, nameof(path), 500);
        var settings = OpenSettings.Create(store, game, engineVersion, usmap, strict);
        return await Task.Run(() =>
        {
            var (assetPath, asset) = ToolAssets.Open(file, settings);
            var exportIndex = ToolAssets.ResolveExport(asset, export);

            var roots = startPath != null
                ? PropertyTreeExpander.GetChildren(
                    PropertyLocator.Locate(asset, exportIndex, startPath)?.Property
                        ?? throw new McpException($"No property at path '{Untrusted.Clean(startPath, 200)}'."),
                    startPath, asset)
                : PropertyTreeExpander.GetExportRoot(asset.Exports[exportIndex], asset);

            var nodes = new List<TreeNodeDto>();
            var truncated = Walk(roots, 0, depth, asset, nodes, cancellationToken);
            return new TreeResult(assetPath, exportIndex, startPath, nodes, truncated, settings.Warnings());
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <returns>True when the node limit cut the walk short.</returns>
    private static bool Walk(IReadOnlyList<PropertyTreeItem> items, int level, int maxDepth, UAsset asset, List<TreeNodeDto> nodes, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nodes.Count >= MaxTreeNodes) return true;

            nodes.Add(new TreeNodeDto(level, Untrusted.Clean(item.DisplayName, 200), Untrusted.Clean(item.Path, 500)));
            if (level + 1 >= maxDepth) continue;
            if (Walk(PropertyTreeExpander.GetChildren(item.Property, item.Path, asset), level + 1, maxDepth, asset, nodes, cancellationToken)) return true;
        }
        return false;
    }

    [McpServerTool(Name = "dump_export", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Lists every leaf property of an export as path and value, optionally under one property path and filtered by a path substring. Long values are cut and the list is capped; narrow with path or filter when truncated is true." + Untrusted.Note)]
    public async Task<DumpResult> DumpExportAsync(
        [Description("Absolute path of the .uasset or .umap; its .uexp must sit beside it.")] string file,
        [Description("Export index, or a unique part of its name. Properties are often on export 3 or 4, not just one: check list_exports.")] string export,
        [Description("Only the properties under this path, like Chains[0].")] string? path = null,
        [Description("Only properties whose path contains this text (case-insensitive).")] string? filter = null,
        [Description("Most properties to return, 1 to 2000 (default 200).")] int maxResults = DefaultResults,
        [Description(ToolParams.Game)] string? game = null,
        [Description(ToolParams.EngineVersion)] string? engineVersion = null,
        [Description(ToolParams.Usmap)] string? usmap = null,
        [Description(ToolParams.Strict)] bool strict = false,
        CancellationToken cancellationToken = default)
    {
        ToolInput.InRange(maxResults, 1, MaxResults, nameof(maxResults));
        var scope = ToolInput.Optional(path, nameof(path), 500);
        var contains = ToolInput.Optional(filter, nameof(filter), 200);
        var settings = OpenSettings.Create(store, game, engineVersion, usmap, strict);
        return await Task.Run(() =>
        {
            var (assetPath, asset) = ToolAssets.Open(file, settings);
            var exportIndex = ToolAssets.ResolveExport(asset, export);

            var results = scope != null
                ? SearchService.PropertiesUnder(asset, asset.FilePath, exportIndex, scope)
                : SearchService.PropertiesForExport(asset, asset.FilePath, exportIndex);

            var shown = new List<PropertyDto>();
            var total = 0;
            var characters = 0;
            foreach (var result in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (contains != null && result.PropertyPath?.Contains(contains, StringComparison.OrdinalIgnoreCase) != true) continue;

                total++;
                if (shown.Count >= maxResults || characters >= MaxDumpCharacters) continue;

                var property = new PropertyDto(Untrusted.Clean(result.PropertyPath, 500), Untrusted.Clean(result.MatchedText, MaxValueLength));
                characters += property.Path.Length + property.Value.Length;
                shown.Add(property);
            }

            return new DumpResult(assetPath, exportIndex, total, shown, total > shown.Count, settings.Warnings());
        }, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "search_assets", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Searches one asset, or every .uasset under a folder, for exports, properties, values or references that match. Terms are case-insensitive substrings unless regex is true. A folder search can take minutes (progress is reported); an asset that cannot be opened or searched is skipped, counted in assetsSkipped and named in warnings, and one whose regular expression runs over its 3 s limit is counted in regexTimeouts, so a missing hit may mean the asset was not searched. Only the first maxResults hits are returned but total counts every match: narrow the terms when truncated is true." + Untrusted.Note)]
    public async Task<SearchResultDto> SearchAssetsAsync(
        [Description("Absolute path of one .uasset, or of a folder searched recursively.")] string path,
        [Description("Match export names.")] string? exportName = null,
        [Description("Match property paths such as Chains[0].Damping (the whole path is tested, not only its last segment).")] string? propertyName = null,
        [Description("Match property values.")] string? value = null,
        [Description("Match imports by dotted object path, e.g. T_Wall.T_Wall.")] string? reference = null,
        [Description("Treat the terms as regular expressions (each match is limited to a few seconds).")] bool regex = false,
        [Description("Most hits to return, 1 to 1000 (default 200).")] int maxResults = DefaultResults,
        [Description(ToolParams.Game)] string? game = null,
        [Description(ToolParams.EngineVersion)] string? engineVersion = null,
        [Description(ToolParams.Usmap)] string? usmap = null,
        [Description(ToolParams.Strict + " Applies to a single asset only.")] bool strict = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ToolInput.InRange(maxResults, 1, MaxSearchHits, nameof(maxResults));
        var terms = new[]
        {
            ToolInput.Optional(exportName, nameof(exportName), 300),
            ToolInput.Optional(propertyName, nameof(propertyName), 300),
            ToolInput.Optional(value, nameof(value), 300),
            ToolInput.Optional(reference, nameof(reference), 300),
        };
        if (terms.All(term => term == null))
            throw new McpException("Give at least one of exportName, propertyName, value and reference.");
        if (regex) ValidateRegexes(terms);

        var (target, isFolder) = ToolPaths.ExistingFileOrFolder(path, nameof(path), ".uasset", ".umap");
        var settings = OpenSettings.Create(store, game, engineVersion, usmap, strict);
        var compare = regex ? TextCompare.Regex : TextCompare.Contains;
        var query = new SearchQuery
        {
            ExportNameTerms = ToTerms(terms[0]),
            ExportNameCompare = compare,
            PropertyNameTerms = ToTerms(terms[1]),
            PropertyNameCompare = compare,
            ValueTerms = ToTerms(terms[2]),
            ValueCompare = compare,
            ReferenceTerms = ToTerms(terms[3]),
            ReferenceCompare = compare,
            RegexTimeout = SearchRegexTimeout,
        };

        SearchOutcome outcome;
        if (isFolder)
        {
            var versions = await Task.Run(
                () => new EngineVersionResolver { DefaultVersion = settings.EngineVersion, Mappings = AssetIo.ResolveMappings(settings.Args), Game = settings.Game },
                cancellationToken).ConfigureAwait(false);
            var sink = new ProgressSink(progress, "Searched assets");
            outcome = await SearchService.SearchAllAsync(
                new LooseFolderAssetSource(target),
                versions,
                query,
                new InlineProgress<SearchProgress>(p => sink.Report((p.Completed, p.Total))),
                maxResults: maxResults,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            outcome = await Task.Run(() => SearchOneAsset(AssetIo.Open(target, settings.Args), target, query, maxResults), cancellationToken).ConfigureAwait(false);
        }

        var hits = outcome.Results
            .Select(r => new SearchHitDto(
                Untrusted.Clean(r.AssetPath, 500), r.ExportIndex, Untrusted.Clean(r.ExportName), r.Kind.ToString(),
                r.PropertyPath is { } hitPath ? Untrusted.Clean(hitPath, 500) : null, Untrusted.Clean(r.MatchedText, MaxValueLength)))
            .ToList();
        var warnings = settings.Warnings().Concat(outcome.Warnings().Select(line => Untrusted.Clean(line, 500))).ToList();

        return new SearchResultDto(
            target, outcome.TotalMatches, hits, outcome.TotalMatches > hits.Count, outcome.AssetsSearched, outcome.AssetsSkipped, outcome.RegexTimeouts, warnings);
    }

    private static SearchOutcome SearchOneAsset(UAsset asset, string path, SearchQuery query, int maxResults)
    {
        var found = SearchService.SearchAsset(asset, path, query).Order(SearchResultOrder.Instance).ToList();
        return new SearchOutcome([.. found.Take(maxResults)], found.Count, 1, 0, 0, []);
    }

    private static IReadOnlyList<ConditionTerm> ToTerms(string? text) => text == null ? [] : [new ConditionTerm(text)];

    private static void ValidateRegexes(IEnumerable<string?> patterns)
    {
        foreach (var pattern in patterns.OfType<string>())
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                throw new McpException($"Not a valid regular expression '{Untrusted.Clean(pattern, 100)}': {Untrusted.Clean(ex.Message, 200)}", ex);
            }
        }
    }
}
