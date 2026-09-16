using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.PropertyAccess;

/// <summary>
/// Unreal's own FName display convention: <see cref="FName.Number"/> 0 means "no suffix",
/// Number K (K &gt;= 1) displays as "_{K-1}" - e.g. a real compiled class's second
/// "AnimGraphNode_KawaiiPhysics" sibling is FName(String="AnimGraphNode_KawaiiPhysics", Number=2),
/// not a literal "AnimGraphNode_KawaiiPhysics_1" string. A cooked, unversioned CDO's own
/// property values instead bake the suffix directly into the string (Number stays 0) - so this
/// only ever *adds* a suffix (Number > 0, string still bare), never double-suffixes an
/// already-baked-in one.
/// </summary>
public static class FNameDisplay
{
    public static string ToDisplayString(FName? name)
    {
        var value = name?.Value?.Value ?? "";
        return name is null || name.Number == 0 ? value : $"{value}_{name.Number - 1}";
    }
}
