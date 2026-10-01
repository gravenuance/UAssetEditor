using Microsoft.Extensions.Logging;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetEditor.Core.Logging;

namespace UAssetEditor.Core.Games.Ff7r;

/// <summary>Finds the DataObject tables in a freshly opened FF7R package and swaps each for a decoded <see cref="Ff7rDataObjectExport"/>.</summary>
public static partial class Ff7rDataObjects
{
    /// <summary>
    /// Decodes every table the package holds and returns a warning for each one it left as it was. Without a mappings
    /// file UAssetAPI can't parse an unversioned export and hands it over as raw bytes, which is read the same way.
    /// Never throws: a table that doesn't decode cleanly is simply not replaced.
    /// </summary>
    public static IReadOnlyList<string> Install(UAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var warnings = new List<string>();
        byte[]? package = null;
        for (var i = 0; i < asset.Exports.Count; i++)
        {
            var export = asset.Exports[i];
            if (!IsCandidate(export, out var className)) continue;

            var label = $"export {i + 1} ({className})";
            var body = export is RawExport raw ? RawBody(raw) : ReadBody(asset, export, ref package);
            if (body is null)
            {
                Warn(warnings, asset, $"{label} was left undecoded: its bytes could not be read back from the package");
                continue;
            }

            var parsed = Ff7rTableCodec.TryParse(body, asset, out var failure);
            if (parsed is null)
            {
                Warn(warnings, asset, $"{label} was left undecoded: {failure}");
                continue;
            }

            var (layout, rows) = parsed.Value;

            var table = export.ConvertToChildExport<Ff7rDataObjectExport>();
            table.Adopt(layout, rows);
            asset.Exports[i] = table;
        }

        return warnings;
    }

    private static bool IsCandidate(Export export, out string className)
    {
        className = string.Empty;
        if (export is not RawExport && (export.GetType() != typeof(NormalExport) || export is not NormalExport { Data.Count: 0 })) return false;
        try
        {
            className = export.GetExportClassType()?.Value?.Value ?? string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return false;
        }

        return className.Contains("DataObject", StringComparison.Ordinal);
    }

    private static byte[]? RawBody(RawExport raw) =>
        raw.Data is { } data && data.Length == raw.SerialSize ? data : null;

    /// <summary>
    /// The export's exact bytes, read from the package files. UAssetAPI has already consumed the leading bytes of a parsed
    /// export, so the table's header is only there in the file; what it kept as <see cref="Export.Extras"/> must be the tail.
    /// </summary>
    private static byte[]? ReadBody(UAsset asset, Export export, ref byte[]? package)
    {
        if (string.IsNullOrEmpty(asset.FilePath) || !File.Exists(asset.FilePath)) return null;
        try
        {
            package ??= asset.PathToStream(asset.FilePath).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var extras = export.Extras ?? [];
        if (export.SerialOffset < 0 || export.SerialSize < extras.Length || export.SerialOffset + export.SerialSize > package.Length) return null;
        var body = package.AsSpan((int)export.SerialOffset, (int)export.SerialSize);
        return body[^extras.Length..].SequenceEqual(extras) ? body.ToArray() : null;
    }

    private static void Warn(List<string> warnings, UAsset asset, string message)
    {
        warnings.Add(message);
        LogUndecoded(AppLog.For<Ff7rDataObjectExport>(), asset.FilePath ?? string.Empty, message);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "FF7R DataObject table in {Path}: {Message}")]
    private static partial void LogUndecoded(ILogger logger, string path, string message);
}
