using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// Recursively enumerates every property in an export's property tree, descending into
/// struct properties and array elements. Paths look like "Foo.Bar" for a nested struct
/// field and "Tags[2]" for an array element.
/// </summary>
public static class PropertyWalker
{
    public static IEnumerable<PropertyNode> Walk(NormalExport export) => WalkList(export.Data, "");

    private static IEnumerable<PropertyNode> WalkList(List<PropertyData> data, string prefix)
    {
        for (var i = 0; i < data.Count; i++)
        {
            var prop = data[i];
            var name = prop.Name?.Value?.Value ?? "";
            var path = string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";

            yield return new PropertyNode(path, prop, data, i);

            foreach (var node in WalkChildren(prop, path))
                yield return node;
        }
    }

    private static IEnumerable<PropertyNode> WalkChildren(PropertyData prop, string path)
    {
        switch (prop)
        {
            case StructPropertyData structProp:
                foreach (var node in WalkList(structProp.Value, path))
                    yield return node;
                break;

            case ArrayPropertyData { Value: { } elements }:
                for (var j = 0; j < elements.Length; j++)
                {
                    var elementPath = $"{path}[{j}]";
                    var element = elements[j];
                    yield return new PropertyNode(elementPath, element, elements, j);

                    foreach (var node in WalkChildren(element, elementPath))
                        yield return node;
                }
                break;
        }
    }
}
