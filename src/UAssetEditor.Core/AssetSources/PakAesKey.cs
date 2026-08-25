namespace UAssetEditor.Core.AssetSources;

/// <summary>Parses the hex AES key text shared by every pak-opening entry point in the app (Load, Repack, Unpack, Pack) into the raw key bytes <see cref="PakAssetSource"/>/<see cref="PakRepacker"/>/<see cref="PakPacker"/> take. An optional "0x" prefix is tolerated; empty/whitespace means "no key" (an unencrypted pak).</summary>
public static class PakAesKey
{
    public static byte[]? Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);

        hex = hex.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        return hex.Length == 0 ? null : Convert.FromHexString(hex);
    }
}
