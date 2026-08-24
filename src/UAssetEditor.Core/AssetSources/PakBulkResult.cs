namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// The outcome of a multi-entry pak operation (<see cref="PakPacker.Build"/>,
/// <see cref="PakRepacker.Build"/>, <see cref="PakUnpacker.Unpack"/>). All three share this
/// shape for consistency, though what a failure means differs by direction: a read-side
/// operation (<see cref="PakUnpacker.Unpack"/>) can report many failed entries and still
/// succeed on the rest, since writing loose files to disk has no ordering dependency between
/// entries. A write-side operation (building a new pak) can report at most one - a crash
/// there aborts the whole attempt, since a pak writer session has no resume capability.
/// </summary>
public sealed class PakBulkResult
{
    public int SucceededCount { get; init; }
    public IReadOnlyList<(string Entry, string Reason)> FailedEntries { get; init; } = Array.Empty<(string, string)>();

    public bool HasFailures => FailedEntries.Count > 0;
}
