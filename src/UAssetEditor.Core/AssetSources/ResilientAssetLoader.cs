using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetEditor.Core.AssetSources;

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
    {
        try
        {
            return new UAsset(path, engineVersion, mappings);
        }
        catch
        {
            return new UAsset(path, engineVersion, mappings, CustomSerializationFlags.SkipParsingExports);
        }
    }
}
