using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// Recursively enumerates every property in an export's property tree - the searchable/
/// editable counterpart to <see cref="PropertyTreeExpander"/>'s per-level tree-view
/// children, walking the whole subtree at once instead of one level at a time. Descends
/// into struct fields, array elements, and map entries; for a <see cref="DataTableExport"/>,
/// walks its rows (which live in a separate <c>Table.Data</c> list, not the export's own,
/// normally-empty <c>Data</c>) instead. Paths look like "Foo.Bar" for a nested struct field,
/// "Tags[2]" for an array element, and "Scores[Alice]" for a map entry keyed by "Alice".
/// </summary>
public static class PropertyWalker
{
    public static IEnumerable<PropertyNode> Walk(NormalExport export)
    {
        var asset = export.Asset;
        return export is DataTableExport dataTable
            ? WalkRows(dataTable.Table.Data, asset)
            : WalkList(export.Data, "", asset);
    }

    private static IEnumerable<PropertyNode> WalkRows(List<StructPropertyData> rows, UAsset asset)
    {
        foreach (var row in rows)
        {
            var path = PropertyPaths.Child("", row);

            // Unlike a plain top-level property, a row isn't an element of a
            // List<PropertyData> the same way (DataTable rows are strongly typed as
            // List<StructPropertyData>), so there's no in-place owner list to hand out
            // for whole-row structural mutation - only its own fields (walked below) get
            // one, via the struct case in WalkChildren.
            yield return new PropertyNode(path, row, null, -1);

            foreach (var node in WalkChildren(row, path, asset))
                yield return node;
        }
    }

    private static IEnumerable<PropertyNode> WalkList(List<PropertyData> data, string prefix, UAsset asset)
    {
        for (var i = 0; i < data.Count; i++)
        {
            var prop = data[i];
            var path = PropertyPaths.Child(prefix, prop);

            yield return new PropertyNode(path, prop, data, i);

            foreach (var node in WalkChildren(prop, path, asset))
                yield return node;
        }
    }

    private static IEnumerable<PropertyNode> WalkChildren(PropertyData prop, string path, UAsset asset)
    {
        switch (prop)
        {
            case StructPropertyData structProp:
                foreach (var node in WalkList(structProp.Value, path, asset))
                    yield return node;
                break;

            case ArrayPropertyData { Value: { } elements }:
                for (var j = 0; j < elements.Length; j++)
                {
                    var elementPath = PropertyPaths.ArrayElement(path, j);
                    var element = elements[j];
                    yield return new PropertyNode(elementPath, element, elements, j);

                    foreach (var node in WalkChildren(element, elementPath, asset))
                        yield return node;
                }
                break;

            case MapPropertyData { Value: { } entries }:
                foreach (var (key, value) in entries)
                {
                    var keyText = PropertyValueAccessor.AsSearchableString(key, asset) ?? key.Name?.Value?.Value ?? "?";
                    var entryPath = PropertyPaths.MapEntry(path, keyText);

                    // Same as a DataTable row: a map's backing TMap isn't a
                    // List<PropertyData>, so entries (like rows) don't get an in-place
                    // owner list - only a value's own nested fields/elements do.
                    yield return new PropertyNode(entryPath, value, null, -1);

                    foreach (var node in WalkChildren(value, entryPath, asset))
                        yield return node;
                }
                break;
        }
    }
}
