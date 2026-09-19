using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Why an asset that opened is nonetheless missing its property content. A degraded open is
/// easy to mistake for a genuinely empty asset: exports still list with real names and types,
/// they just carry no properties, so a caller that doesn't check this reports "no properties"
/// for what is actually a parse failure.
/// </summary>
public sealed record AssetOpenDiagnostics(bool ExportsSkipped, Exception? FullParseFailure)
{
    public static readonly AssetOpenDiagnostics Complete = new(false, null);
}

/// <summary>
/// Opens a .uasset file with a fallback: a full structured parse can still throw for an
/// asset UAssetAPI's own per-property/per-export fail-safes (<c>UnknownPropertyData</c>,
/// <c>RawExport</c>) couldn't route around. Rather than losing the asset entirely, this
/// retries with <see cref="CustomSerializationFlags.SkipParsingExports"/>, which reads
/// every export as raw bytes instead of structured properties - the header, name map,
/// import table, and each export's name/type/size are still retained, just not its
/// property content. If even that throws, the asset genuinely can't be opened and the
/// exception propagates, matching every existing caller's "skip this asset" handling.
/// </summary>
public static class ResilientAssetLoader
{
    public static UAsset Open(string path, EngineVersion engineVersion, Usmap? mappings)
        => Open(path, engineVersion, mappings, out _);

    /// <param name="diagnostics">
    /// Reports whether the structured parse succeeded. When <see cref="AssetOpenDiagnostics.ExportsSkipped"/>
    /// is true the returned asset has no property data at all, and
    /// <see cref="AssetOpenDiagnostics.FullParseFailure"/> carries the exception that caused it.
    /// </param>
    public static UAsset Open(string path, EngineVersion engineVersion, Usmap? mappings, out AssetOpenDiagnostics diagnostics)
    {
        try
        {
            var asset = new UAsset(path, engineVersion, mappings);
            diagnostics = AssetOpenDiagnostics.Complete;
            return asset;
        }
        catch (Exception ex)
        {
            var asset = new UAsset(path, engineVersion, mappings, CustomSerializationFlags.SkipParsingExports);
            diagnostics = new AssetOpenDiagnostics(true, ex);
            return asset;
        }
    }

    /// <summary>Opens without the fallback, so a parse failure surfaces as its real exception - the only way to see <em>why</em> an asset degrades.</summary>
    public static UAsset OpenStrict(string path, EngineVersion engineVersion, Usmap? mappings)
        => new(path, engineVersion, mappings);
}
