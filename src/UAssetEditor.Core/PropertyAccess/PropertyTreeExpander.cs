using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// One tree node's worth of a property: its full path (the same path scheme
/// <see cref="PropertyWalker"/>'s flat walk uses, so it can be re-located the same way),
/// how to display it, and the property itself.
/// </summary>
public sealed record PropertyTreeItem(string Path, string DisplayName, PropertyData Property);

/// <summary>
/// Supplies the Browse tree's property children one level at a time, filtered down to only
/// struct/array/map properties that actually have something inside them - the tree is
/// purely a navigator for tables nested in tables, so a scalar leaf property (an int, a
/// string, a bool, ...) never appears as a tree entry at all. Reaching a leaf's actual
/// value means double-clicking the table that directly contains it open into the edit grid
/// (see <see cref="Search.SearchService.PropertiesUnder"/>), the same way opening a whole
/// export already works. For a <see cref="DataTableExport"/>, the root is its rows
/// (<c>Table.Data</c>) rather than the export's own, normally-empty <c>Data</c>.
/// </summary>
public static class PropertyTreeExpander
{
    public static IReadOnlyList<PropertyTreeItem> GetExportRoot(Export export, UAsset asset)
    {
        IEnumerable<PropertyData>? data = export switch
        {
            DataTableExport dataTable => dataTable.Table.Data,
            NormalExport normal => normal.Data,
            _ => null,
        };

        return data == null ? [] : FilterFields(data, "");
    }

    public static IReadOnlyList<PropertyTreeItem> GetChildren(PropertyData property, string path, UAsset asset) => property switch
    {
        StructPropertyData s => FilterFields(s.Value, path),
        ArrayPropertyData { Value: { } elements } => FilterElements(elements, path),
        MapPropertyData map => FilterEntries(map.Value, path, asset),
        _ => [],
    };

    private static List<PropertyTreeItem> FilterFields(IEnumerable<PropertyData> properties, string prefix)
    {
        var items = new List<PropertyTreeItem>();
        foreach (var property in properties)
        {
            var count = ChildCount(property);
            if (count == 0) continue;

            var name = property.Name?.Value?.Value ?? "";
            items.Add(new PropertyTreeItem(PropertyPaths.Child(prefix, property), $"{name} ({count})", property));
        }
        return items;
    }

    private static List<PropertyTreeItem> FilterElements(PropertyData[] elements, string prefix)
    {
        var items = new List<PropertyTreeItem>();
        for (var i = 0; i < elements.Length; i++)
        {
            var count = ChildCount(elements[i]);
            if (count == 0) continue;

            items.Add(new PropertyTreeItem(PropertyPaths.ArrayElement(prefix, i), $"[{i}] ({count})", elements[i]));
        }
        return items;
    }

    private static List<PropertyTreeItem> FilterEntries(TMap<PropertyData, PropertyData> entries, string prefix, UAsset asset)
    {
        var items = new List<PropertyTreeItem>();
        foreach (var (key, value) in entries)
        {
            var count = ChildCount(value);
            if (count == 0) continue;

            var keyText = PropertyValueAccessor.AsSearchableString(key, asset) ?? key.Name?.Value?.Value ?? "?";
            items.Add(new PropertyTreeItem(PropertyPaths.MapEntry(prefix, keyText), $"{keyText} ({count})", value));
        }
        return items;
    }

    private static int ChildCount(PropertyData property) => property switch
    {
        StructPropertyData s => s.Value.Count,
        ArrayPropertyData { Value: { } elements } => elements.Length,
        MapPropertyData map => map.Value.Count,
        _ => 0,
    };
}
