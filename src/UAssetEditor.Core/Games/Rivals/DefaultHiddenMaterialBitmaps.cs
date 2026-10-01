using System.Globalization;

namespace UAssetEditor.Core.Games.Rivals;

/// <summary>Reads the per-LOD <c>DefaultHiddenMaterials</c> bitmap list given on the command line.</summary>
public static class DefaultHiddenMaterialBitmaps
{
    /// <summary>
    /// Parses masks separated by <c>,</c> <c>;</c> or <c>|</c>, each decimal or <c>0x</c> hex (<c>0x5,0x1</c> or <c>5;1</c>).
    /// </summary>
    /// <exception cref="FormatException">The text is empty or a mask is not an unsigned 64-bit number.</exception>
    public static IReadOnlyList<ulong> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException("The hidden-material bitmap list is empty; give one mask per LOD, e.g. 0x5,0x1.");
        }

        var bitmaps = new List<ulong>();
        foreach (var rawPart in text.Split(',', ';', '|'))
        {
            var part = rawPart.Trim();
            if (part.Length == 0) continue;

            var isHex = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            var digits = isHex ? part[2..] : part;
            var style = isHex ? NumberStyles.HexNumber : NumberStyles.Integer;
            if (!ulong.TryParse(digits, style, CultureInfo.InvariantCulture, out var value))
            {
                throw new FormatException(
                    $"'{part}' is not a valid hidden-material bitmap; use a decimal or 0x-prefixed hex number that fits in 64 bits.");
            }

            bitmaps.Add(value);
        }

        if (bitmaps.Count == 0)
        {
            throw new FormatException("The hidden-material bitmap list is empty; give one mask per LOD, e.g. 0x5,0x1.");
        }

        return bitmaps;
    }
}
