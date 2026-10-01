using System.Buffers.Binary;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;

namespace UAssetEditor.Core.Games.Rivals;

/// <summary>
/// Counts a SkeletalMesh's material slots from the native bytes that follow its properties, without decoding them.
/// repak-rivals gets the same number from its fork's <c>SkeletalMeshExport</c>; the vendored UAssetAPI has no such
/// export, so this re-derives just the count with the same rules: the material count is the first plausible
/// (count, negative package index) pair in the first 100 bytes, else the count after the strip flags and bounds, else
/// the first pattern match over the whole body. Read-only: the export's bytes are never changed.
/// </summary>
internal static class SkeletalMeshMaterialCounter
{
    private const int MaxStructuredCount = 100;
    private const int MaxPatternCount = 50;
    private const int MinPackageIndex = -10000;
    private const int LegacyBoundsBytes = 28;
    private const int LargeCoordinateBoundsBytes = 56;
    private const int StripFlagsBytes = 2;
    private const int PlainMaterialBytes = 40;
    private const int TaggedMaterialBytes = 44;

    /// <summary>True for a SkeletalMesh export (the class name ends in "SkeletalMesh", as in the fork).</summary>
    public static bool IsSkeletalMesh(Export export)
    {
        var className = ClassName(export);
        return className != null && className.EndsWith("SkeletalMesh", StringComparison.Ordinal);
    }

    /// <summary>The material slot count, or 0 when the export is not a SkeletalMesh or its bytes do not show one.</summary>
    public static int Count(Export export, UAsset asset)
    {
        if (!IsSkeletalMesh(export)) return 0;

        var extras = export.Extras;
        if (extras == null || extras.Length == 0) return 0;

        var structured = CountStructured(extras, asset);
        return structured > 0 ? structured : CountByPattern(extras);
    }

    private static string? ClassName(Export export)
    {
        try
        {
            return export.GetExportClassType()?.Value?.Value;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static int CountStructured(byte[] extras, UAsset asset)
    {
        var countOffset = DetectCountOffset(extras);
        if (countOffset <= 0)
        {
            var boundsBytes = asset.ObjectVersionUE5 >= ObjectVersionUE5.LARGE_WORLD_COORDINATES
                ? LargeCoordinateBoundsBytes
                : LegacyBoundsBytes;
            countOffset = StripFlagsBytes + boundsBytes;
        }

        if (countOffset + sizeof(int) > extras.Length) return 0;

        var count = ReadInt(extras, countOffset);
        if (count < 1 || count > MaxStructuredCount) return 0;

        // Every material is at least 40 bytes; a count that runs past the body was misread.
        return countOffset + sizeof(int) + (count * PlainMaterialBytes) <= extras.Length ? count : 0;
    }

    private static int DetectCountOffset(byte[] extras)
    {
        if (extras.Length < 40) return -1;

        var end = Math.Min(100, extras.Length - 8);
        for (var offset = 20; offset < end; offset++)
        {
            var count = ReadInt(extras, offset);
            var packageIndex = ReadInt(extras, offset + 4);
            if (count > 0 && count <= MaxPatternCount && packageIndex < 0 && packageIndex > MinPackageIndex)
            {
                return offset;
            }
        }

        return -1;
    }

    private static int CountByPattern(byte[] extras)
    {
        foreach (var materialBytes in new[] { PlainMaterialBytes, TaggedMaterialBytes })
        {
            for (var i = 4; i < extras.Length - (materialBytes * 2); i++)
            {
                var count = ReadInt(extras, i);
                if (count < 1 || count > MaxPatternCount) continue;

                var firstPackageIndex = ReadInt(extras, i + 4);
                if (firstPackageIndex >= 0 || firstPackageIndex < MinPackageIndex) continue;

                if (LooksLikeMaterials(extras, i, count, materialBytes)
                    && i + 4 + (count * materialBytes) <= extras.Length)
                {
                    return count;
                }
            }
        }

        return 0;
    }

    private static bool LooksLikeMaterials(byte[] extras, int countOffset, int count, int materialBytes)
    {
        for (var m = 0; m < Math.Min(count, 5); m++)
        {
            var materialOffset = countOffset + 4 + (m * materialBytes);
            if (materialOffset + materialBytes > extras.Length) return false;

            var packageIndex = ReadInt(extras, materialOffset);
            if (packageIndex > 0 || packageIndex < MinPackageIndex) return false;

            if (ReadInt(extras, materialOffset + 4) < 0) return false;
        }

        return true;
    }

    private static int ReadInt(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)));
}
