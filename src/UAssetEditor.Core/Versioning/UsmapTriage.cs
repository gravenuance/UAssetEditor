using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Games;
using UAssetEditor.Core.PropertyAccess;
using UAssetEditor.Core.Search;

namespace UAssetEditor.Core.Versioning;

/// <summary>How an asset reads under an old and a new usmap; when exports disagree, the earliest of NewFails, Misread, OldFails, BothFail wins.</summary>
public enum TriageStatus
{
    /// <summary>Both read it identically: the schema change does not touch it.</summary>
    Same,

    /// <summary>Both read some export, with different values: it was built for one of them and the other misreads it.</summary>
    Misread,

    /// <summary>Some export parses only under the old usmap: typically built for it.</summary>
    NewFails,

    /// <summary>Some export parses only under the new usmap: typically built for it already.</summary>
    OldFails,

    /// <summary>The exports both usmaps parse read the same, but some parse under neither, so they went unchecked.</summary>
    BothFail,

    /// <summary>Tagged (versioned) properties are read by name, so no usmap change can misread them.</summary>
    Tagged,

    /// <summary>The file could not be opened at all.</summary>
    Error,
}

/// <param name="Failed">The export did not parse into properties (read as raw bytes).</param>
/// <param name="Lines">"path=value" per property, then the length of the native data after them.</param>
public sealed record ExportReading(string Name, bool Failed, IReadOnlyList<string> Lines);

/// <param name="Tagged">The package stores tagged properties, which no usmap is involved in reading.</param>
public sealed record AssetReading(bool Tagged, IReadOnlyList<ExportReading> Exports);

/// <param name="Detail">The affected exports, e.g. "Default__X_C: differs at Foo.Bar; Mesh: new-raw"; empty when none.</param>
public sealed record TriageResult(string Path, TriageStatus Status, string Detail);

/// <summary>
/// After a game patch changes the usmap, tells which unversioned assets read differently under the new one. Each file is
/// opened once per usmap and every export's properties compared, so an asset that only references a changed class is
/// not flagged; one that serialises it is.
/// </summary>
public static class UsmapTriage
{
    public static string Label(TriageStatus status) => status switch
    {
        TriageStatus.Same => "OK-SAME",
        TriageStatus.Misread => "MISREAD",
        TriageStatus.NewFails => "NEW-FAILS",
        TriageStatus.OldFails => "OLD-FAILS",
        TriageStatus.BothFail => "BOTH-FAIL",
        TriageStatus.Tagged => "TAGGED",
        TriageStatus.Error => "ERROR",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown triage status."),
    };

    /// <summary>Opens <paramref name="path"/> under each usmap and compares the readings. Safe to call in parallel with shared usmaps.</summary>
    public static TriageResult TriageFile(string path, EngineVersion version, Usmap oldMappings, Usmap newMappings, Game game)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(oldMappings);
        ArgumentNullException.ThrowIfNull(newMappings);

        // A .uasset without its .uexp opens "successfully" with garbage exports, which would pass for a misread.
        if (path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.ChangeExtension(path, ".uexp")))
            return new TriageResult(path, TriageStatus.Error, "no .uexp beside it");

        try
        {
            var before = Read(ResilientAssetLoader.Open(path, version, oldMappings.CreateAssetScope(), game), path);
            var after = Read(ResilientAssetLoader.Open(path, version, newMappings.CreateAssetScope(), game), path);
            var (status, detail) = Compare(before, after);
            return new TriageResult(path, status, detail);
        }
        // ResilientAssetLoader already falls back to raw exports for a bad parse; what reaches here is a file it can't open.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or ArgumentException)
        {
            return new TriageResult(path, TriageStatus.Error, $"{ex.GetType().Name}: {OneLine(ex.Message)}");
        }
    }

    /// <summary>Every export's properties as text, in order.</summary>
    public static AssetReading Read(UAsset asset, string path)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var exports = new List<ExportReading>(asset.Exports.Count);
        for (var i = 0; i < asset.Exports.Count; i++)
        {
            var export = asset.Exports[i];
            var lines = new List<string>();
            if (export is NormalExport normal)
            {
                foreach (var row in SearchService.PropertiesForExport(asset, path, i)) lines.Add($"{row.PropertyPath}={row.MatchedText}");
                // Native data after the properties starts wherever a misread left off, so a shifted schema changes its length.
                lines.Add($"extras={normal.Extras?.Length ?? -1}");
            }
            exports.Add(new ExportReading(FNameDisplay.ToDisplayString(export.ObjectName), export is RawExport, lines));
        }
        return new AssetReading(!asset.HasUnversionedProperties, exports);
    }

    public static (TriageStatus Status, string Detail) Compare(AssetReading before, AssetReading after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Tagged) return (TriageStatus.Tagged, "");

        var detail = new List<string>();
        bool oldFails = false, newFails = false, bothFail = false, differs = false;
        for (var i = 0; i < Math.Max(before.Exports.Count, after.Exports.Count); i++)
        {
            var a = i < before.Exports.Count ? before.Exports[i] : null;
            var b = i < after.Exports.Count ? after.Exports[i] : null;
            if (a is null || b is null)
            {
                differs = true;
                detail.Add($"{(a ?? b)!.Name}: only {(a is null ? "new" : "old")}"); // the loop bound guarantees one of them exists
                continue;
            }

            if (a.Failed && b.Failed)
            {
                bothFail = true;
                detail.Add($"{a.Name}: both-raw");
            }
            else if (a.Failed || b.Failed)
            {
                oldFails |= a.Failed;
                newFails |= b.Failed;
                detail.Add($"{a.Name}: {(a.Failed ? "old-raw" : "new-raw")}");
            }
            else if (FirstDifference(a.Lines, b.Lines) is { } where)
            {
                differs = true;
                detail.Add($"{a.Name}: differs at {where}");
            }
        }

        // The most telling export decides: one that parses under neither usmap (often a Blueprint CDO) says nothing about the change.
        var status = newFails ? TriageStatus.NewFails
            : differs ? TriageStatus.Misread
            : oldFails ? TriageStatus.OldFails
            : bothFail ? TriageStatus.BothFail
            : TriageStatus.Same;
        return (status, string.Join("; ", detail));
    }

    // The property path of the first line that differs, or null when the readings match.
    private static string? FirstDifference(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var shared = Math.Min(a.Count, b.Count);
        for (var i = 0; i < shared; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return PathOf(a[i]);
        }
        return a.Count == b.Count ? null : PathOf((a.Count > b.Count ? a : b)[shared]);
    }

    private static string PathOf(string line)
    {
        var at = line.IndexOf('=', StringComparison.Ordinal);
        return at >= 0 ? line[..at] : line;
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Replace('\t', ' ');
}
