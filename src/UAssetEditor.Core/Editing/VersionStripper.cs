using UAssetAPI;

namespace UAssetEditor.Core.Editing;

/// <summary>
/// Drops a package summary's engine and custom versions while keeping tagged properties. IoStore packers
/// require an unversioned summary; tagged properties are matched by name, so a stock-engine cook still loads
/// in a game that changed engine classes, which unversioned (schema-positional) properties would not.
/// </summary>
public static class VersionStripper
{
    public static void Strip(UAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.HasUnversionedProperties) throw new ArgumentException("The package already uses unversioned properties; nothing to strip.", nameof(asset));
        asset.IsUnversioned = true;
    }
}
