using System.Text.RegularExpressions;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.AssetSources.IoStore;

/// <summary>
/// Maps this app's <see cref="EngineVersion"/> (UAssetAPI's naming, e.g. "VER_UE5_3") to the
/// engine version strings retoc's own <c>--version</c> flag accepts (e.g. "UE5_3") - confirmed
/// against the real vendored retoc.exe's <c>to-zen --help</c> output: <c>UE4_25, UE4_26,
/// UE4_27, UE5_0, UE5_1, UE5_2, UE5_3, UE5_4, UE5_5, UE5_6, UE5_7</c>. IoStore itself only
/// exists from UE4.25 onward, so anything older has no retoc equivalent by construction.
/// </summary>
public static partial class EngineVersionMapping
{
    private static readonly HashSet<string> RetocSupportedVersions = new(StringComparer.Ordinal)
    {
        "UE4_25", "UE4_26", "UE4_27",
        "UE5_0", "UE5_1", "UE5_2", "UE5_3", "UE5_4", "UE5_5", "UE5_6", "UE5_7",
    };

    // Same shape MainViewModel.EngineVersionNamePattern already parses UAssetAPI's enum names
    // with - kept independent (not shared) since this one deliberately ignores the "EA" (Early
    // Access) group retoc has no separate concept of, mapping an EA version to its non-EA
    // major.minor instead of failing it outright.
    [GeneratedRegex(@"^VER_UE(?<major>\d+)_(?<minor>\d+)(?:EA)?$")]
    private static partial Regex NamePattern();

    /// <summary>Returns the matching retoc <c>--version</c> value, or null if this engine version has no IoStore/retoc equivalent (older than UE4.25, or not one of UAssetAPI's real per-release values).</summary>
    public static string? ToRetocVersion(EngineVersion version)
    {
        var match = NamePattern().Match(version.ToString());
        if (!match.Success) return null;

        var candidate = $"UE{match.Groups["major"].Value}_{match.Groups["minor"].Value}";
        return RetocSupportedVersions.Contains(candidate) ? candidate : null;
    }
}
