using System.ComponentModel;
using ModelContextProtocol.Server;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.AssetSources.IoStore;
using UAssetEditor.Core.AssetSources.PakWorker;
using UAssetEditor.Core.Games;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Tools that look inside .pak and IoStore archives without changing them. Windows only: they run the bundled pak worker and retoc.</summary>
[McpServerToolType]
internal sealed class ArchiveReadTools(MappingsStore store)
{
    private const int DefaultEntries = 500;
    private const int MaxEntries = 5000;

    [McpServerTool(Name = "pak_info", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Reads a .pak's header: its mount point and format version. Windows only.")]
    public async Task<PakInfoResult> PakInfoAsync(
        [Description("Absolute path of the .pak.")] string pak,
        [Description(ToolParams.Game)] string? game = null,
        [Description(ToolParams.AesKey)] string? aesKey = null,
        CancellationToken cancellationToken = default)
    {
        ToolRuntime.RequireWindows("pak_info");
        var path = ToolPaths.ExistingFile(pak, nameof(pak), ".pak");
        var key = OpenSettings.Create(store, game, null, null, aesKey: aesKey).AesKey;

        var info = await ToolRuntime.UsePakWorkerBlockingAsync(() => PakMountPointReader.Read(path, key), cancellationToken).ConfigureAwait(false);
        return new PakInfoResult(path, Untrusted.Clean(info.MountPoint, 500), info.Version.ToString());
    }

    [McpServerTool(Name = "pak_list", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Lists the entries of a .pak, sorted, optionally only those whose path contains filter, and marks the .uasset entries. A real game pak holds hundreds of thousands of entries: pass a filter, and read matched and truncated. Windows only." + Untrusted.Note)]
    public async Task<PakListResult> PakListAsync(
        [Description("Absolute path of the .pak.")] string pak,
        [Description("Only entries whose path contains this text (case-insensitive).")] string? filter = null,
        [Description("Most entries to return, 1 to 5000 (default 500).")] int maxResults = DefaultEntries,
        [Description(ToolParams.Game)] string? game = null,
        [Description(ToolParams.AesKey)] string? aesKey = null,
        CancellationToken cancellationToken = default)
    {
        ToolRuntime.RequireWindows("pak_list");
        ToolInput.InRange(maxResults, 1, MaxEntries, nameof(maxResults));
        var path = ToolPaths.ExistingFile(pak, nameof(pak), ".pak");
        var contains = ToolInput.Optional(filter, nameof(filter), 300);
        var key = OpenSettings.Create(store, game, null, null, aesKey: aesKey).AesKey;

        var entries = await ToolRuntime.UsePakWorkerAsync(async () =>
        {
            using var reader = new PakReaderHandle(PakWorkerProcess.Shared, path, key);
            await reader.OpenAsync(cancellationToken).ConfigureAwait(false);
            return reader.Entries;
        }, cancellationToken).ConfigureAwait(false);

        var page = EntryPage.Of(entries, contains, maxResults);
        var shown = page.Shown
            .Select(e => new EntryDto(Untrusted.Clean(e, 500), e.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new PakListResult(path, entries.Count, page.Matched, shown, page.Truncated, page.Note);
    }

    [McpServerTool(Name = "iostore_list", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Lists the chunk paths of an IoStore .utoc container through retoc, optionally only those containing filter. Use it to find the exact entry path to_legacy's filter expects. Windows only." + Untrusted.Note)]
    public async Task<IoStoreListResult> IoStoreListAsync(
        [Description("Absolute path of the .utoc.")] string utoc,
        [Description("Only entries whose path contains this text (case-insensitive).")] string? filter = null,
        [Description("Most entries to return, 1 to 5000 (default 500).")] int maxResults = DefaultEntries,
        [Description(ToolParams.Game)] string? game = null,
        [Description(ToolParams.AesKey)] string? aesKey = null,
        CancellationToken cancellationToken = default)
    {
        ToolRuntime.RequireWindows("iostore_list");
        ToolInput.InRange(maxResults, 1, MaxEntries, nameof(maxResults));
        var path = ToolPaths.ExistingFile(utoc, nameof(utoc), ".utoc");
        var contains = ToolInput.Optional(filter, nameof(filter), 300);
        var key = OpenSettings.Create(store, game, null, null, aesKey: aesKey).AesKey;

        var entries = await RetocProcess.ListAsync(path, key, cancellationToken).ConfigureAwait(false);
        var page = EntryPage.Of(entries, contains, maxResults);
        var shown = page.Shown.Select(e => Untrusted.Clean(e, 500)).ToList();

        return new IoStoreListResult(path, page.Matched, shown, page.Truncated, page.Note);
    }

    /// <summary>The first <c>max</c> entries containing a filter, in case-insensitive order, and how many matched in all.</summary>
    private sealed record EntryPage(int Matched, IReadOnlyList<string> Shown)
    {
        public bool Truncated => Matched > Shown.Count;

        public string Note => Truncated ? $"Showing the first {Shown.Count} of {Matched} matches: narrow the filter." : "";

        public static EntryPage Of(IEnumerable<string> entries, string? contains, int max)
        {
            var matched = entries.Where(e => contains == null || e.Contains(contains, StringComparison.OrdinalIgnoreCase)).ToList();
            // Order then Take sorts only the kept entries, not a game pak's hundreds of thousands.
            return new EntryPage(matched.Count, [.. matched.Order(StringComparer.OrdinalIgnoreCase).Take(max)]);
        }
    }
}
