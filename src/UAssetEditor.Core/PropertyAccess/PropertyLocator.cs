using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>Re-locates a property by the path search and dump print for it.</summary>
public static class PropertyLocator
{
    public static PropertyNode? Locate(UAsset asset, int exportIndex, string? propertyPath) =>
        Resolve(asset, exportIndex, propertyPath, create: false);

    /// <summary>
    /// Like <see cref="Locate"/>, but a named field missing because it sits at its default (unversioned
    /// data omits those) is created from the schema first. Array elements and map entries are never invented.
    /// </summary>
    public static PropertyNode? LocateOrCreate(UAsset asset, int exportIndex, string? propertyPath) =>
        Resolve(asset, exportIndex, propertyPath, create: true);

    /// <summary>
    /// The export's own top-level property list - the root <see cref="ArrayElementEditor"/>/
    /// <see cref="StructFieldEditor"/> mutate when a whole property (not just a field within an
    /// existing struct) is missing entirely, e.g. a rigid body whose every <c>DefaultInstance</c>
    /// field sits at its engine default omits the whole struct property, not just its fields.
    /// </summary>
    public static IList<PropertyData>? LocateExportData(UAsset asset, int exportIndex)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (exportIndex < 0 || exportIndex >= asset.Exports.Count) return null;
        return asset.Exports[exportIndex] is NormalExport export ? export.Data : null;
    }

    /// <summary>
    /// Re-locates an array element's own owning array and index by path (e.g. "Chains[3]") -
    /// the counterpart to <see cref="Locate"/> for structural edits (<see cref="ArrayElementEditor"/>),
    /// which need the array itself to mutate, not just the element sitting at that path.
    /// </summary>
    public static (ArrayPropertyData Array, int Index)? LocateArrayElement(UAsset asset, int exportIndex, string elementPath)
    {
        ArgumentNullException.ThrowIfNull(elementPath);

        if (!PropertyPaths.TrySplitTrailingIndex(elementPath, out var parentPath, out var index)) return null;

        var parent = Locate(asset, exportIndex, parentPath);
        return parent?.Property is ArrayPropertyData array ? (array, index) : null;
    }

    private static PropertyNode? Resolve(UAsset asset, int exportIndex, string? propertyPath, bool create)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (propertyPath == null || exportIndex < 0 || exportIndex >= asset.Exports.Count) return null;
        if (asset.Exports[exportIndex] is not NormalExport export) return null;

        // Following the path costs its depth; the whole-tree walk stays as the fallback for shapes it doesn't follow (map keys, table rows).
        if (export is not DataTableExport && export.Data != null && PathSegments.TryParse(propertyPath, out var segments))
        {
            var descent = Descend(asset, export.Data, segments, create);
            if (descent.Found != null) return descent.Found;
            if (create && descent.Outcome == DescentOutcome.Missing) return null;
        }

        return PropertyWalker.Walk(export).FirstOrDefault(n => n.Path == propertyPath);
    }

    private enum DescentOutcome { Found, Missing, Unsupported }

    private readonly record struct Descent(DescentOutcome Outcome, PropertyNode? Found);

    private static Descent Descend(UAsset asset, List<PropertyData> root, IReadOnlyList<PathSegment> segments, bool create)
    {
        var created = new List<(List<PropertyData> Owner, PropertyData Field)>();
        var descent = Descend(asset, root, segments, create, created);

        // A lookup that fails part-way leaves no half-built fields behind.
        if (descent.Found == null)
        {
            foreach (var (owner, field) in created) owner.Remove(field);
        }
        return descent;
    }

    private static Descent Descend(UAsset asset, List<PropertyData> root, IReadOnlyList<PathSegment> segments, bool create,
        List<(List<PropertyData> Owner, PropertyData Field)> created)
    {
        var list = root;
        StructPropertyData? listOwner = null;
        PropertyNode? node = null;
        var path = "";

        foreach (var segment in segments)
        {
            if (segment.Name is { } name)
            {
                if (node != null)
                {
                    if (node.Property is not StructPropertyData s) return new(DescentOutcome.Missing, null);
                    list = s.Value ??= [];
                    listOwner = s;
                }

                path = path.Length == 0 ? name : $"{path}.{name}";
                var index = IndexOfName(list, name);
                if (index < 0)
                {
                    if (!create || SchemaFieldFactory.Create(asset, list, listOwner, name) is not { } field) return new(DescentOutcome.Missing, null);
                    list.Add(field);
                    created.Add((list, field));
                    index = list.Count - 1;
                }

                node = new PropertyNode(path, list[index], list, index);
                continue;
            }

            if (node?.Property is MapPropertyData) return new(DescentOutcome.Unsupported, null);
            if (node?.Property is not ArrayPropertyData { Value: { } elements } || segment.Index is not { } i || i < 0 || i >= elements.Length)
                return new(DescentOutcome.Missing, null);

            path = PropertyPaths.ArrayElement(path, i);
            node = new PropertyNode(path, elements[i], elements, i);
        }

        return node == null ? new(DescentOutcome.Missing, null) : new(DescentOutcome.Found, node);
    }

    private static int IndexOfName(List<PropertyData> list, string name)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (FNameDisplay.ToDisplayString(list[i].Name) == name) return i;
        }
        return -1;
    }
}
