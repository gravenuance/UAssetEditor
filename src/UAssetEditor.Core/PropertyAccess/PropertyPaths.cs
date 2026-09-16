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
        // FNameDisplay reconstructs a "_N" suffix from FName.Number when the raw string doesn't
        // already carry it (a real compiled class's Nth "Foo" sibling) - without it, every such
        // sibling's raw Name.Value collapses to the exact same bare string here, so search/dump/
        // tree can't tell node 5 from node 23 apart. Confirmed on a real legacy-converted asset:
        // 43 distinct top-level "AnimGraphNode_KawaiiPhysics_N" properties all walked as the one
        // indistinguishable path "AnimGraphNode_KawaiiPhysics" before this.
        var name = FNameDisplay.ToDisplayString(property.Name);
        return string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";
    }

    public static string ArrayElement(string prefix, int index) => $"{prefix}[{index}]";

    public static string MapEntry(string prefix, string keyText) => $"{prefix}[{keyText}]";

    /// <summary>
    /// Splits a path's trailing "[N]" bracket segment (as produced by <see cref="ArrayElement"/>
    /// or <see cref="MapEntry"/>) into the prefix before it and the parsed integer inside - used
    /// to find an array element's own owning array by path without walking the whole tree again
    /// (see <see cref="PropertyLocator.LocateArrayElement"/>). Fails for a map entry whose key
    /// text isn't itself a plain integer; the caller still has to confirm the resolved property
    /// actually is an array, since a map keyed by ints would otherwise look identical here.
    /// </summary>
    public static bool TrySplitTrailingIndex(string path, out string parentPath, out int index)
    {
        var openBracket = path.LastIndexOf('[');
        if (openBracket < 0 || path[^1] != ']' ||
            !int.TryParse(path.AsSpan(openBracket + 1, path.Length - openBracket - 2), out index))
        {
            parentPath = path;
            index = -1;
            return false;
        }

        parentPath = path[..openBracket];
        return true;
    }
}
