using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.Core.Games.Rivals;

/// <summary>One asset the patch rewrote, with its path relative to the patched folder.</summary>
public sealed record PatchedAsset(string RelativePath, KawaiiPhysicsPortResult Result);

/// <summary>
/// Runs the Marvel Rivals patches (<see cref="KawaiiPhysicsLegacyPorter"/>) over every <c>.uasset</c> under a folder,
/// in place, the way repak-rivals' directory fix does: open without mappings first, retry with the usmap when that
/// throws, and write an asset back only when something changed. Callers that must keep their source untouched
/// patch a copy.
/// </summary>
public static class RivalsAssetPatcher
{
    private static readonly byte[] MarkerAscii = Encoding.ASCII.GetBytes("KawaiiPhysics");
    private static readonly byte[] MarkerUtf16 = Encoding.Unicode.GetBytes("KawaiiPhysics");

    /// <summary>Patches every package under <paramref name="directory"/>; returns the ones that were rewritten, in path order.</summary>
    /// <param name="usmapPath">The game's .usmap, used when a package cannot be opened without mappings.</param>
    /// <param name="warn">Receives one line per asset that was opened but could not be fully read.</param>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="InvalidDataException">A package could not be opened or the options do not fit it; the message names the asset.</exception>
    public static IReadOnlyList<PatchedAsset> PatchDirectory(
        string directory,
        string usmapPath,
        KawaiiPhysicsPortOptions options,
        Action<string>? warn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(usmapPath);
        ArgumentNullException.ThrowIfNull(options);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Folder not found: {directory}");

        var patched = new List<PatchedAsset>();
        var files = Directory.EnumerateFiles(directory, "*.uasset", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToList();

        Usmap? mappings = null;
        foreach (var file in files)
        {
            if (!MayNeedPatching(file, options)) continue;

            try
            {
                var result = PatchAsset(file, usmapPath, ref mappings, options, warn);
                if (result.Changed) patched.Add(new PatchedAsset(Path.GetRelativePath(directory, file), result));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidDataException($"{Path.GetRelativePath(directory, file)}: {ex.Message}", ex);
            }
        }

        return patched;
    }

    private static KawaiiPhysicsPortResult PatchAsset(
        string uassetPath,
        string usmapPath,
        ref Usmap? mappings,
        KawaiiPhysicsPortOptions options,
        Action<string>? warn)
    {
        var asset = OpenLikeRepakRivals(uassetPath, usmapPath, ref mappings);
        var result = KawaiiPhysicsLegacyPorter.PortLegacyAnimNodes(asset, options);

        if (result.Changed)
        {
            PackageWriter.Write(asset, uassetPath);
        }
        else if (asset.HasUnversionedProperties && mappings == null && asset.Exports.Any(e => e is RawExport))
        {
            warn?.Invoke(
                $"{Path.GetFileName(uassetPath)}: unversioned package opened without mappings, so its exports were not read and nothing was patched.");
        }

        return result;
    }

    /// <summary>
    /// Opens without mappings first and only loads the usmap when that throws, exactly as repak-rivals does. It also forces
    /// the .uasset/.uexp split on, as repak-rivals' loader does, so a single-file package is written back as a pair.
    /// </summary>
    private static UAsset OpenLikeRepakRivals(string uassetPath, string usmapPath, ref Usmap? mappings)
    {
        try
        {
            return new UAsset(uassetPath, EngineVersion.VER_UE5_3) { UseSeparateBulkDataFiles = true };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!File.Exists(usmapPath)) throw new FileNotFoundException($"Mappings file not found: {usmapPath}", usmapPath, ex);
        }

        mappings ??= new Usmap(usmapPath);
        return new UAsset(uassetPath, EngineVersion.VER_UE5_3, mappings) { UseSeparateBulkDataFiles = true };
    }

    /// <summary>
    /// When only the KawaiiPhysics port runs, a package that never mentions KawaiiPhysics (in its names or its export
    /// bytes) cannot contain a node, so it is not opened at all. The hidden-material patches inspect every mesh.
    /// </summary>
    private static bool MayNeedPatching(string uassetPath, KawaiiPhysicsPortOptions options)
    {
        if (!options.PatchKawaiiPhysics || options.PatchDefaultHiddenMaterials || options.HasBitmapOverride) return true;

        return ContainsMarker(uassetPath) || ContainsMarker(Path.ChangeExtension(uassetPath, ".uexp"));
    }

    private static bool ContainsMarker(string path)
    {
        if (!File.Exists(path)) return false;

        var bytes = File.ReadAllBytes(path);
        return bytes.AsSpan().IndexOf(MarkerAscii) >= 0 || bytes.AsSpan().IndexOf(MarkerUtf16) >= 0;
    }
}
