using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using UAssetEditor.Core.Games;
using UAssetEditor.Mcp.Tools;

namespace UAssetEditor.Mcp.Tests.Rig;

/// <summary>The tool classes as the server builds them, over a temp folder and a usmap store of its own, so no test
/// reads or writes the user's real store.</summary>
internal sealed class McpRig : IDisposable
{
    public McpRig()
    {
        Folder = new TempFolder();
        Store = new MappingsStore(Folder.Combine("maps"));
        Assets = new AssetReadTools(Store);
        Edits = new AssetEditTools(Store);
        Rulesets = new RulesetTools(Store);
        ArchiveReads = new ArchiveReadTools(Store);
        ArchiveWrites = new ArchiveWriteTools(Store, NullLogger<ArchiveWriteTools>.Instance);
        Usmaps = new UsmapTools(Store);
    }

    public TempFolder Folder { get; }

    public MappingsStore Store { get; }

    public AssetReadTools Assets { get; }

    public AssetEditTools Edits { get; }

    public RulesetTools Rulesets { get; }

    public ArchiveReadTools ArchiveReads { get; }

    public ArchiveWriteTools ArchiveWrites { get; }

    public UsmapTools Usmaps { get; }

    public static CancellationToken Cancelled { get; } = new(canceled: true);

    public void Dispose() => Folder.Dispose();
}

/// <summary>Collects what a tool reports through its progress token.</summary>
internal sealed class CollectedProgress : IProgress<ProgressNotificationValue>
{
    private readonly List<ProgressNotificationValue> _values = [];

    public IReadOnlyList<ProgressNotificationValue> Values
    {
        get
        {
            lock (_values) return [.. _values];
        }
    }

    public void Report(ProgressNotificationValue value)
    {
        lock (_values) _values.Add(value);
    }
}
