using UAssetAPI;
using UAssetEditor.Core.Games;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// What a pak is read and written as: its AES key and its <see cref="PakVariant"/>. The one value every pak open, repack and
/// reload decision compares, so the key and the variant can never be taken from different places. Two formats are equal
/// when they hold the same key bytes and the same variant, however the key was spelt.
/// </summary>
public sealed record PakFormat
{
    private readonly string _aesKeyHex;

    private PakFormat(string aesKeyHex, PakVariant variant)
    {
        _aesKeyHex = aesKeyHex;
        Variant = variant;
    }

    public PakVariant Variant { get; }

    /// <summary>A fresh copy of the key bytes, or null for an unencrypted pak.</summary>
    public byte[]? CopyAesKey() => PakAesKey.Parse(_aesKeyHex);

    public static PakFormat Of(byte[]? aesKey, PakVariant variant) =>
        new(aesKey is { Length: > 0 } ? Convert.ToHexString(aesKey) : "", variant);

    /// <summary>
    /// The format for a game selection: the game's variant, or <paramref name="keptVariant"/> when no game is selected (so a pak
    /// already open keeps the variant it was opened with); the key is <paramref name="aesKeyHex"/> (an empty one means none).
    /// </summary>
    /// <exception cref="FormatException">The key text is not hex.</exception>
    public static PakFormat Resolve(GameProfile? profile, string? aesKeyHex, PakVariant keptVariant = PakVariant.Standard) =>
        Of(PakAesKey.Parse(aesKeyHex ?? ""), profile?.PakVariant ?? keptVariant);
}
