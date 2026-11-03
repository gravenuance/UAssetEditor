using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Structs;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// Adds a DataTable row as a deep copy of an existing one under a new row name. Rows live in
/// <see cref="DataTableExport.Table"/>, not in an array property, so array-element duplication
/// can't reach them; the game looks rows up by name, so the new name must be unique.
/// </summary>
public static class DataTableRowDuplicator
{
    /// <summary>Appends the copy as the table's last row and returns it.</summary>
    public static StructPropertyData Duplicate(UAsset asset, int exportIndex, string sourceRow, string newRow)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRow);
        ArgumentException.ThrowIfNullOrWhiteSpace(newRow);
        if (exportIndex < 0 || exportIndex >= asset.Exports.Count || asset.Exports[exportIndex] is not DataTableExport { Table.Data: { } rows })
            throw new ArgumentException($"Export {exportIndex} isn't a DataTable.", nameof(exportIndex));

        var source = rows.Find(r => RowName(r) == sourceRow)
            ?? throw new ArgumentException($"No row named '{sourceRow}'.", nameof(sourceRow));
        if (rows.Exists(r => RowName(r) == newRow))
            throw new ArgumentException($"A row named '{newRow}' already exists.", nameof(newRow));

        var clone = (StructPropertyData)source.Clone();
        clone.Name = FNameDisplay.Parse(asset, newRow);
        rows.Add(clone);
        return clone;
    }

    private static string RowName(StructPropertyData row) => FNameDisplay.ToDisplayString(row.Name);
}
