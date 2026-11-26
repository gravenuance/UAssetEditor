using System.Buffers;
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

/// <summary>What became of the backups a write asked for.</summary>
/// <param name="Placed">Every ".bak" (and the first save's ".orig.bak") is where it belongs; false when one could not be put there.</param>
/// <param name="Stranded">Copies of the previous files still under their temporary names, the only good copy when <paramref name="Placed"/> is false.</param>
/// <param name="Backup">Where the package file's ".bak" goes (its .uexp's sits beside it); null when there was no file to back up.</param>
/// <param name="Original">Where the package file's ".orig.bak" goes; null when there was no file to back up.</param>
public sealed record BackupResult(bool Placed, IReadOnlyList<string> Stranded, string? Backup, string? Original);

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
    /// Backs up the package (its .uasset and, when present, .uexp) before writing it: "&lt;file&gt;.bak" is the state before this
    /// save, replaced each time, and "&lt;file&gt;.orig.bak" the state before the first save, written once and never replaced. A
    /// write that fails without having changed either file leaves no backup behind, and any backup already there stays as it
    /// was; one that fails after changing a file keeps the backup, since it is then the only good copy.
    /// </summary>
    /// <param name="backupFolder">Where backups go; null puts each next to its file with a ".bak" suffix.</param>
    /// <returns>Whether the backups are in place; a failed move does not fail a save that went through.</returns>
    public static BackupResult WriteWithBackup(UAsset asset, string path, string? backupFolder)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(path);
        return WithBackup(path, backupFolder, () => Write(asset, path));
    }

    private readonly record struct StagedBackup(string Original, string Staged, string Backup, string Vanilla);

    /// <summary>The backup handling of <see cref="WriteWithBackup"/> around any <paramref name="write"/> of the package at <paramref name="path"/>.</summary>
    internal static BackupResult WithBackup(string path, string? backupFolder, Action write, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        // Staged under a name of this save's own, so an existing backup is only replaced once there is reason to and
        // another save of the same file cannot pick up or overwrite this copy.
        var stagingId = Guid.NewGuid().ToString("N"); // StagingIdLength lowercase hex digits, which SweepStaleStaging relies on
        var staged = new List<StagedBackup>();
        try
        {
            foreach (var file in (string[])[path, Path.ChangeExtension(path, ".uexp")])
            {
                if (!File.Exists(file)) continue;
                var backup = BackupPathResolver.Resolve(file, backupFolder);
                var stagedPath = $"{backup}.{stagingId}.tmp";
                File.Copy(file, stagedPath, overwrite: true);
                staged.Add(new StagedBackup(file, stagedPath, backup, BackupPathResolver.ResolveOriginal(file, backupFolder)));
                // Copy keeps the source's write time; stamping it says when this save began, which the sweep reads as its age.
                File.SetLastWriteTimeUtc(stagedPath, clock.GetUtcNow().UtcDateTime);
            }
        }
        catch
        {
            foreach (var copy in staged) TryDelete(copy.Staged);
            throw;
        }

        var written = false;
        BackupResult result;
        try
        {
            write();
            written = true;
        }
        finally
        {
            result = SettleBackups(path, staged, written, clock);
        }
        return result;
    }

    /// <summary>Never throws, so the write's own outcome is what the caller sees. When in doubt the copy is kept as the backup.</summary>
    private static BackupResult SettleBackups(string path, List<StagedBackup> staged, bool written, TimeProvider clock)
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

        var placed = true;
        var stranded = new List<string>();
        foreach (var copy in staged)
        {
            try
            {
                if (untouched)
                {
                    File.Delete(copy.Staged);
                    continue;
                }

                placed &= KeepVanilla(copy);
                File.Move(copy.Staged, copy.Backup, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                placed = false;
                if (!untouched) stranded.Add(copy.Staged);
                LogBackupNotPlaced(AppLog.For<UAsset>(), copy.Backup, copy.Staged, ex);
            }
        }
        if (placed && !untouched)
        {
            foreach (var copy in staged) SweepStaleStaging(copy, clock);
        }
        var main = staged.Find(copy => copy.Original == path);
        return main == default ? new BackupResult(placed, stranded, null, null) : new BackupResult(placed, stranded, main.Backup, main.Vanilla);
    }

    // No save holds its staged copy this long, so an older one was left by a save that was killed or could not place it.
    private static readonly TimeSpan StaleStagingAge = TimeSpan.FromHours(1);

    /// <summary>
    /// Deletes "&lt;backup&gt;.&lt;staging id&gt;.tmp" copies that earlier saves left next to <paramref name="copy"/>'s backup, but
    /// only those byte for byte the same as the file, its ".bak" or its ".orig.bak": any other may be the only copy of some
    /// state (a save reported it stranded, or was killed mid-write, or it belongs to a same-named file sharing the backup
    /// folder), so it is kept and logged.
    /// </summary>
    private static void SweepStaleStaging(StagedBackup copy, TimeProvider clock)
    {
        var folder = Path.GetDirectoryName(copy.Backup)!; // Resolve returns a full file path, so it has a folder
        var prefix = Path.GetFileName(copy.Backup) + ".";
        List<string> candidates;
        try
        {
            candidates = [.. Directory.EnumerateFiles(folder, prefix + "*.tmp")
                .Where(path => IsStagingName(Path.GetFileName(path), prefix) && !string.Equals(path, copy.Staged, StringComparison.OrdinalIgnoreCase))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStagingNotSwept(AppLog.For<UAsset>(), folder, ex);
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var path in candidates)
        {
            try
            {
                if (now - File.GetLastWriteTimeUtc(path) <= StaleStagingAge) continue;
                if (IsDuplicate(path, copy)) File.Delete(path);
                else LogStagingKept(AppLog.For<UAsset>(), path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogStagingNotSwept(AppLog.For<UAsset>(), path, ex);
            }
        }
    }

    private static bool IsDuplicate(string leftover, StagedBackup copy) =>
        ((string[])[copy.Original, copy.Backup, copy.Vanilla]).Any(file => File.Exists(file) && SameContents(leftover, file));

    private const int StagingIdLength = 32;

    private static bool IsStagingName(string name, string prefix) =>
        name.Length == prefix.Length + StagingIdLength + ".tmp".Length
        && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        && !name.AsSpan(prefix.Length, StagingIdLength).ContainsAnyExcept(LowerHex);

    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    /// <summary>Copies the earliest state at hand to the file's ".orig.bak", unless an earlier save already did: a ".bak" left by
    /// a save from before ".orig.bak" existed, else the file as it was before this save.</summary>
    private static bool KeepVanilla(StagedBackup copy)
    {
        if (File.Exists(copy.Vanilla)) return true;
        try
        {
            File.Copy(File.Exists(copy.Backup) ? copy.Backup : copy.Staged, copy.Vanilla, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogBackupNotPlaced(AppLog.For<UAsset>(), copy.Vanilla, copy.Staged, ex);
            return File.Exists(copy.Vanilla);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Leftover staged backups at {Path} could not be checked or removed")]
    private static partial void LogStagingNotSwept(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Path} was left by an earlier save and matches no file or backup now on disk; it is kept, as it may be the only copy of that state")]
    private static partial void LogStagingKept(ILogger logger, string path);

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
