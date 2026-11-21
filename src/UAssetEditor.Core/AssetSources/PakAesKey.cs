namespace UAssetEditor.Core.AssetSources;

/// <summary>Parses the hex AES key text shared by every pak-opening entry point in the app (Load, Repack, Unpack, Pack) into the raw key bytes <see cref="PakAssetSource"/>/<see cref="PakRepacker"/>/<see cref="PakPacker"/> take. An optional "0x" prefix is tolerated; empty/whitespace means "no key" (an unencrypted pak).</summary>
public static class PakAesKey
{
    /// <summary>The size of a pak AES key in bytes (AES-256).</summary>
    public const int Length = 32;

    /// <summary>What <see cref="Parse"/> says for any key text that is not exactly <see cref="Length"/> bytes of hex; it never repeats the key.</summary>
    public const string Requirement = "The AES key must be exactly 32 bytes: 64 hex digits, with or without a leading 0x.";

    /// <exception cref="FormatException">The text is not hex, or not exactly <see cref="Length"/> bytes of it.</exception>
    public static byte[]? Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);

        hex = hex.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length == 0) return null;

        byte[] key;
        try
        {
            key = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            throw new FormatException(Requirement);
        }

        return key.Length == Length ? key : throw new FormatException(Requirement);
    }
}
