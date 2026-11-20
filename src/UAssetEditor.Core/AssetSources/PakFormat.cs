using UAssetAPI;
using UAssetEditor.Core.Games;

namespace UAssetEditor.Core.AssetSources;

/// <summary>
/// What a pak is read and written as: its AES key and its <see cref="PakVariant"/>. The one value every pak open, repack and
/// reload decision compares, so the key and the variant can never be taken from different places. Two formats are equal
/// when they hold the same key bytes and the same variant, however the key was spelt. The key is always the one the user
/// types (the game profile's); <see cref="CopyCipherKey"/> turns it into what the pak is actually encrypted with.
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

    /// <summary>A fresh copy of the key as the user holds it (the game profile's form, the one IoStore uses), or null for an unencrypted pak.</summary>
    public byte[]? CopyAesKey() => PakAesKey.Parse(_aesKeyHex);

    /// <summary>
    /// A fresh copy of the key the pak's cipher is keyed with, or null for an unencrypted pak: the key itself, except that a
    /// Marvel Rivals pak is encrypted with it with each 4-byte word byte-reversed (as retoc does for its companion paks).
    /// The one place the two forms are told apart: every pak read and write takes the key from here.
    /// </summary>
    /// <exception cref="FormatException">A Marvel Rivals key is not a whole number of 4-byte words.</exception>
    public byte[]? CopyCipherKey()
    {
        var key = CopyAesKey();
        if (key == null || Variant != PakVariant.MarvelRivals) return key;

        if (key.Length % sizeof(uint) != 0)
            throw new FormatException($"A Marvel Rivals pak key must be a whole number of 4-byte words, not {key.Length} bytes.");
        for (var word = 0; word < key.Length; word += sizeof(uint))
            Array.Reverse(key, word, sizeof(uint));
        return key;
    }

    /// <summary><see cref="CopyCipherKey"/> as the hex the pak worker takes, or null for an unencrypted pak.</summary>
    public string? CipherKeyHex() => CopyCipherKey() is { } key ? Convert.ToHexString(key) : null;

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
