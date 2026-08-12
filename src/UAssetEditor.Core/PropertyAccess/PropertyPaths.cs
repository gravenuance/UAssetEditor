using UAssetAPI.PropertyTypes.Objects;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// The one path scheme <see cref="PropertyWalker"/> (whole-subtree, for search/edit) and
/// <see cref="PropertyTreeExpander"/> (one level at a time, for the Browse tree) both build
/// paths with, so a path either one produces always means the same property to the other -
/// this is what lets double-clicking a table node reached by drilling into the tree scope
/// straight into the matching slice of a full <see cref="PropertyWalker.Walk"/>. "Foo.Bar"
/// for a nested struct field, "Tags[2]" for an array element, "Scores[Alice]" for a map
/// entry keyed by "Alice".
/// </summary>
internal static class PropertyPaths
{
    public static string Child(string prefix, PropertyData property)
    {
        var name = property.Name?.Value?.Value ?? "";
        return string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";
    }

    public static string ArrayElement(string prefix, int index) => $"{prefix}[{index}]";

    public static string MapEntry(string prefix, string keyText) => $"{prefix}[{keyText}]";
}
