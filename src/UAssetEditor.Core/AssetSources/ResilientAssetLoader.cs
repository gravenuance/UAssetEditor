using System.Runtime.CompilerServices;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetEditor.Core.Games;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// Why an asset that opened is nonetheless missing its property content. A degraded open is
/// easy to mistake for a genuinely empty asset: exports still list with real names and types,
/// they just carry no properties, so a caller that doesn't check this reports "no properties"
/// for what is actually a parse failure.
/// </summary>
/// <param name="Warnings">What the game's post-open step left alone, e.g. a data table that did not decode; empty when nothing needs attention.</param>
public sealed record AssetOpenDiagnostics(bool ExportsSkipped, Exception? FullParseFailure, IReadOnlyList<string> Warnings)
{
    public static readonly AssetOpenDiagnostics Complete = new(false, null, []);
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
    // Asset sources return only the UAsset, so the open warnings travel with the instance instead.
    private static readonly ConditionalWeakTable<UAsset, IReadOnlyList<string>> OpenWarnings = new();

    /// <summary>What the game's post-open step left alone when <paramref name="asset"/> was opened here; empty when nothing needs attention.</summary>
    public static IReadOnlyList<string> WarningsFor(UAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return OpenWarnings.TryGetValue(asset, out var warnings) ? warnings : [];
    }

    public static UAsset Open(string path, EngineVersion engineVersion, Usmap? mappings, Game game)
        => Open(path, engineVersion, mappings, game, out _);

    /// <param name="diagnostics">
    /// Reports whether the structured parse succeeded. When <see cref="AssetOpenDiagnostics.ExportsSkipped"/>
    /// is true the returned asset has no property data at all, and
    /// <see cref="AssetOpenDiagnostics.FullParseFailure"/> carries the exception that caused it.
    /// </param>
    public static UAsset Open(string path, EngineVersion engineVersion, Usmap? mappings, Game game, out AssetOpenDiagnostics diagnostics)
    {
        UAsset asset;
        Exception? failure = null;
        try
        {
            asset = new UAsset(path, engineVersion, mappings);
        }
        catch (Exception ex)
        {
            asset = new UAsset(path, engineVersion, mappings, CustomSerializationFlags.SkipParsingExports);
            failure = ex;
        }

        var warnings = PostOpen(asset, game);
        diagnostics = failure is null ? new AssetOpenDiagnostics(false, null, warnings) : new AssetOpenDiagnostics(true, failure, warnings);
        return asset;
    }

    /// <summary>Opens without the fallback, so a parse failure surfaces as its real exception - the only way to see <em>why</em> an asset degrades.</summary>
    public static UAsset OpenStrict(string path, EngineVersion engineVersion, Usmap? mappings, Game game)
    {
        var asset = new UAsset(path, engineVersion, mappings);
        PostOpen(asset, game);
        return asset;
    }

    internal static IReadOnlyList<string> PostOpen(UAsset asset, Game game)
    {
        var warnings = GameProfile.For(game)?.PostOpen(asset) ?? [];
        if (warnings.Count > 0) OpenWarnings.AddOrUpdate(asset, warnings);
        return warnings;
    }
}
