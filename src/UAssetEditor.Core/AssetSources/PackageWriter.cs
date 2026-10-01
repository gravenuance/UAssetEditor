using Microsoft.Extensions.Logging;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetEditor.Core.Logging;

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
public static partial class PackageWriter
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

    /// <summary>
    /// Backs up the package (its .uasset and, when present, .uexp) before writing it. A write that fails without having
    /// changed either file leaves no backup behind, and any backup already there stays as it was; one that fails after
    /// changing a file keeps the backup, since it is then the only good copy.
    /// </summary>
    /// <param name="backupFolder">Where backups go; null puts each next to its file with a ".bak" suffix.</param>
    public static void WriteWithBackup(UAsset asset, string path, string? backupFolder)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(path);
        WithBackup(path, backupFolder, () => Write(asset, path));
    }

    /// <summary>The backup handling of <see cref="WriteWithBackup"/> around any <paramref name="write"/> of the package at <paramref name="path"/>.</summary>
    internal static void WithBackup(string path, string? backupFolder, Action write)
    {
        // Staged under a temporary name so an existing backup is only replaced once there is reason to.
        var staged = new List<(string Original, string Staged, string Backup)>();
        try
        {
            foreach (var file in (string[])[path, Path.ChangeExtension(path, ".uexp")])
            {
                if (!File.Exists(file)) continue;
                var backup = BackupPathResolver.Resolve(file, backupFolder);
                File.Copy(file, backup + ".tmp", overwrite: true);
                staged.Add((file, backup + ".tmp", backup));
            }
        }
        catch
        {
            foreach (var (_, stagedPath, _) in staged) TryDelete(stagedPath);
            throw;
        }

        var written = false;
        try
        {
            write();
            written = true;
        }
        finally
        {
            SettleBackups(staged, written);
        }
    }

    /// <summary>Never throws, so the write's own outcome is what the caller sees. When in doubt the copy is kept as the backup.</summary>
    private static void SettleBackups(List<(string Original, string Staged, string Backup)> staged, bool written)
    {
        bool untouched;
        try
        {
            untouched = !written && staged.All(s => File.Exists(s.Original) && SameContents(s.Original, s.Staged));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            untouched = false;
        }

        foreach (var (_, stagedPath, backup) in staged)
        {
            try
            {
                if (untouched) File.Delete(stagedPath);
                else File.Move(stagedPath, backup, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogBackupNotPlaced(AppLog.For<UAsset>(), backup, stagedPath, ex);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogBackupNotPlaced(AppLog.For<UAsset>(), path, path, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup {Backup} could not be put in place; the copy stays at {Staged}")]
    private static partial void LogBackupNotPlaced(ILogger logger, string backup, string staged, Exception exception);

    private static bool SameContents(string first, string second)
    {
        using var a = File.OpenRead(first);
        using var b = File.OpenRead(second);
        if (a.Length != b.Length) return false;

        Span<byte> bufferA = stackalloc byte[8192];
        Span<byte> bufferB = stackalloc byte[8192];
        int read;
        while ((read = a.Read(bufferA)) > 0)
        {
            b.ReadExactly(bufferB[..read]);
            if (!bufferA[..read].SequenceEqual(bufferB[..read])) return false;
        }
        return true;
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
