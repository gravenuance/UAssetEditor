namespace UAssetEditor.Core.AssetSources.IoStore;

/// <summary>What <see cref="IoStoreLegacyConverter.ConvertAsync"/> did besides converting the requested entries.</summary>
/// <param name="OwnEntryCount">Entries of the container itself when imports were resolved through its enclosing Paks folder; 0 when it was converted alone.</param>
/// <param name="RemovedBaseGameBulkFiles">Output-relative paths of bulk files that came from the base game and were deleted.</param>
/// <param name="BaseGameBulkKept">True when a <see cref="RetocLayer.Modded"/> conversion wrote a .pak, which can't be pruned, so it may hold base-game bulk data.</param>
public sealed record LegacyConversionResult(int OwnEntryCount, IReadOnlyList<string> RemovedBaseGameBulkFiles, bool BaseGameBulkKept)
{
    public bool ResolvedThroughPaksFolder => OwnEntryCount > 0;
}

/// <summary>
/// Converts one .utoc to legacy format, resolving its imports through the enclosing Paks folder (see
/// <see cref="RetocLayerResolver"/>). A <see cref="RetocLayer.Modded"/> conversion keeps only what the container ships:
/// when it replaces a package but not that package's bulk data, retoc reads the bulk file from the base game, and that
/// file is deleted from the output, so repacking the output adds no chunks the mod never had.
/// </summary>
public static class IoStoreLegacyConverter
{
    // ".m.ubulk" (memory-mapped bulk data) also ends in ".ubulk"
    private static readonly string[] BulkExtensions = [".ubulk", ".uptnl"];
    private static readonly string[] PackageExtensions = [".uasset", ".umap"];

    /// <param name="filters">Entries to convert; empty converts every entry of <paramref name="utocPath"/>.</param>
    public static async Task<LegacyConversionResult> ConvertAsync(
        string utocPath, string output, RetocLayer layer, IReadOnlyList<string> filters, byte[]? aesKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utocPath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(filters);

        using var scope = RetocLayerResolver.Resolve(utocPath, layer);
        IReadOnlyList<string> ownEntries = scope.InputDirectory == utocPath ? [] : await RetocProcess.ListAsync(utocPath, aesKey, cancellationToken).ConfigureAwait(false);

        // With no entries of its own to scope the merged view to, converting it would convert the whole game
        if (ownEntries.Count == 0)
        {
            await RetocProcess.ConvertToLegacyAsync(utocPath, output, filters, aesKey, cancellationToken).ConfigureAwait(false);
            return new LegacyConversionResult(0, [], BaseGameBulkKept: false);
        }

        await RetocProcess.ConvertToLegacyAsync(scope.InputDirectory, output, filters.Count > 0 ? filters : ownEntries, aesKey, cancellationToken).ConfigureAwait(false);

        if (layer != RetocLayer.Modded)
            return new LegacyConversionResult(ownEntries.Count, [], BaseGameBulkKept: false);
        if (string.Equals(Path.GetExtension(output), ".pak", StringComparison.OrdinalIgnoreCase))
            return new LegacyConversionResult(ownEntries.Count, [], BaseGameBulkKept: true);
        return new LegacyConversionResult(ownEntries.Count, RemoveBulkFilesNotShipped(output, ownEntries), BaseGameBulkKept: false);
    }

    /// <summary>
    /// Deletes each bulk file under <paramref name="outputDirectory"/> whose package is one of
    /// <paramref name="containerEntries"/> but which is not itself one of them. Bulk files of packages the container
    /// doesn't ship (base-game entries asked for by filter) are kept. Returns the deleted files' output-relative paths.
    /// </summary>
    internal static IReadOnlyList<string> RemoveBulkFilesNotShipped(string outputDirectory, IReadOnlyList<string> containerEntries)
    {
        var shipped = containerEntries.Select(NormalizeEntryPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = new List<string>();

        foreach (var file in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories))
        {
            if (!BulkExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) continue;

            var relative = Path.GetRelativePath(outputDirectory, file).Replace('\\', '/');
            if (shipped.Contains(relative)) continue;

            var packageStem = StripBulkExtension(relative);
            if (!PackageExtensions.Any(ext => shipped.Contains(packageStem + ext))) continue;

            File.Delete(file);
            removed.Add(relative);
        }
        return removed;
    }

    // retoc lists entries under their mount point ("../../../Marvel/Content/...") and writes them without it
    private static string NormalizeEntryPath(string entry)
    {
        var path = entry.Replace('\\', '/').TrimStart('/');
        while (path.StartsWith("../", StringComparison.Ordinal))
            path = path[3..];
        return path;
    }

    private static string StripBulkExtension(string path)
    {
        var withoutExtension = path[..^Path.GetExtension(path).Length];
        return withoutExtension.EndsWith(".m", StringComparison.OrdinalIgnoreCase) ? withoutExtension[..^2] : withoutExtension;
    }
}
