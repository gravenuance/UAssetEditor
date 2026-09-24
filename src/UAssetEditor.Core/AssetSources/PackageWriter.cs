using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.AssetSources;

/// <summary>Where an export's unparsed native tail sits, measured from the start of the export data (.uexp).</summary>
public readonly record struct ExportTail(long Start, long Length);

/// <summary>
/// UE 5.3+ packages keep inline bulk data (e.g. a texture's small mips) in the export's native tail and record
/// its offset in the header's data-resource table. UAssetAPI writes that table unchanged, so any edit that
/// resizes an export's properties leaves the offsets pointing at the wrong bytes: the game then loads garbage
/// mips and falls back to its default texture. Moving each inline resource with its export's tail fixes that.
/// </summary>
public static class InlineDataResources
{
    /// <summary>Returns how many inline resources moved. Throws if one can't be placed, rather than write a broken package.</summary>
    public static int Shift(IList<FObjectDataResource> resources, IReadOnlyList<ExportTail> before, IReadOnlyList<ExportTail> after)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Count != after.Count) throw new ArgumentException("Export counts differ before and after writing.", nameof(after));

        var moved = 0;
        for (var i = 0; i < resources.Count; i++)
        {
            var resource = resources[i];
            if (!IsInline(resource) || resource.OuterIndex is not { } outer || !outer.IsExport()) continue;
            var e = outer.Index - 1;
            if (e < 0 || e >= before.Count) throw new InvalidOperationException($"Data resource {i} belongs to export {e + 1}, which doesn't exist.");
            var delta = after[e].Start - before[e].Start;
            if (delta == 0) continue;
            if (after[e].Length != before[e].Length)
                throw new InvalidOperationException($"Export {e + 1}'s native data changed size; its inline data resources can't be relocated safely.");
            if (resource.SerialOffset < before[e].Start || resource.SerialOffset + resource.SerialSize > before[e].Start + before[e].Length)
                throw new InvalidOperationException($"Inline data resource {i} (offset {resource.SerialOffset}) lies outside export {e + 1}'s native data; not relocating it.");
            resource.SerialOffset += delta;
            if (resource.DuplicateSerialOffset >= 0) resource.DuplicateSerialOffset += delta;
            resources[i] = resource;
            moved++;
        }
        return moved;
    }

    private const uint LegacyForceInlinePayload = 0x40;
    private const uint LegacyPayloadInSeparateFile = 0x100;

    // A 5.3 cook marks inline payloads only through the legacy bulk-data flags; newer ones use the Inline flag.
    private static bool IsInline(FObjectDataResource resource) =>
        resource.Flags.HasFlag(EObjectDataResourceFlags.Inline)
        || ((resource.LegacyBulkDataFlags & LegacyForceInlinePayload) != 0 && (resource.LegacyBulkDataFlags & LegacyPayloadInSeparateFile) == 0);
}

/// <summary>Writes a package to disk, keeping inline data resources pointed at their bytes.</summary>
public static class PackageWriter
{
    public static void Write(UAsset asset, string path)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.DataResources is { Count: > 0 } resources && asset.Exports.Count > 0)
        {
            var before = Tails(asset);
            asset.WriteData().Dispose(); // lays the exports out again, updating their serial offsets and sizes
            InlineDataResources.Shift(resources, before, Tails(asset));
        }
        asset.Write(path);
    }

    private static List<ExportTail> Tails(UAsset asset)
    {
        var exportDataStart = asset.Exports.Min(e => e.SerialOffset);
        return [.. asset.Exports.Select(e =>
        {
            var length = e.Extras?.Length ?? 0;
            return new ExportTail(e.SerialOffset - exportDataStart + e.SerialSize - length, length);
        })];
    }
}
