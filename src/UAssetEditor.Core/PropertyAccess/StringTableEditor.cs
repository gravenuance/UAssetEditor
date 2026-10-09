using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// Sets or adds a key's source string in a StringTable asset. Text that names a string-table key the table lacks
/// shows as missing in game even when a .locres translates it, so new keys have to be added here.
/// </summary>
public static class StringTableEditor
{
    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/>, appending it when absent. Returns the previous value, or null when added.</summary>
    public static string? Set(UAsset asset, int exportIndex, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (exportIndex < 0 || exportIndex >= asset.Exports.Count || asset.Exports[exportIndex] is not StringTableExport { Table: { } table })
            throw new ArgumentException($"Export {exportIndex} isn't a StringTable.", nameof(exportIndex));

        // FString equality also compares encodings, so a key read from disk never equals a freshly built one.
        for (var i = 0; i < table.Count; i++)
        {
            if (table.Keys.ElementAt(i).Value != key) continue;
            var previous = table[i].Value;
            table[i] = new FString(value);
            return previous;
        }

        table.Add(new FString(key), new FString(value));
        return null;
    }
}
