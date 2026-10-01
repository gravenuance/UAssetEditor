using System.Globalization;
using UAssetAPI;
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

    /// <summary>
    /// The reverse of <see cref="ToDisplayString"/> for a value typed by the user. A string already in
    /// the name map is kept whole, as before. Otherwise "Base_K" becomes Base with Number K+1 when Base
    /// is in the map and K has no leading zero (Unreal's own split rule), so "E_ARM_4001" names the
    /// same entry the game wrote. Anything else is kept whole.
    /// </summary>
    public static FName Parse(UAsset asset, string text)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(text);

        if (asset.ContainsNameReference(new FString(text)))
            return new FName(asset, text);

        var underscore = text.LastIndexOf('_');
        var digits = underscore < 0 ? "" : text[(underscore + 1)..];
        if (underscore > 0
            && digits.Length is > 0 and <= 9
            && digits.All(char.IsAsciiDigit)
            && (digits.Length == 1 || digits[0] != '0')
            && asset.ContainsNameReference(new FString(text[..underscore])))
        {
            return new FName(asset, text[..underscore], int.Parse(digits, CultureInfo.InvariantCulture) + 1);
        }

        return new FName(asset, text);
    }
}
