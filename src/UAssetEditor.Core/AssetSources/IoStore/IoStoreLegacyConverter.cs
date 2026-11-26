namespace UAssetEditor.Core.AssetSources.IoStore;

/// <summary>What <see cref="IoStoreLegacyConverter.ConvertAsync"/> did besides converting the requested entries.</summary>
/// <param name="ResolvedThroughPaksFolder">Imports were resolved through the container's enclosing Paks folder, not the container alone.</param>
/// <param name="OwnEntryCount">Entries of the container itself, when they were listed; 0 otherwise.</param>
/// <param name="RemovedBaseGameBulkFiles">Output-relative paths of bulk files that came from the base game and were deleted.</param>
/// <param name="BaseGameBulkKept">True when a <see cref="RetocLayer.Modded"/> conversion wrote a .pak, which can't be pruned, so it may hold base-game bulk data.</param>
public sealed record LegacyConversionResult(bool ResolvedThroughPaksFolder, int OwnEntryCount, IReadOnlyList<string> RemovedBaseGameBulkFiles, bool BaseGameBulkKept)
{
    internal static LegacyConversionResult Alone { get; } = new(false, 0, [], BaseGameBulkKept: false);
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
    /// <param name="paksFolder">The game's Paks folder, for a container outside it; null uses the enclosing one, if any.</param>
    public static async Task<LegacyConversionResult> ConvertAsync(
        string utocPath, string output, RetocLayer layer, IReadOnlyList<string> filters, byte[]? aesKey, string? paksFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utocPath);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(filters);
        RefuseOwnFile(utocPath, output);

        using var scope = RetocLayerResolver.Resolve(utocPath, layer, paksFolder);
        if (scope.InputDirectory == utocPath)
        {
            await RetocProcess.ConvertToLegacyAsync(utocPath, output, filters, aesKey, cancellationToken).ConfigureAwait(false);
            return LegacyConversionResult.Alone;
        }

        // The entries scope an unfiltered run and tell a mod's bulk from the base game's. A base-game chunk ships its
        // own bulk, and listing one costs seconds, so a filtered run on it skips the listing
        var prune = layer == RetocLayer.Modded && !RetocLayerResolver.IsBaseGameContainer(utocPath);
        IReadOnlyList<string> ownEntries = filters.Count == 0 || prune
            ? await RetocProcess.ListAsync(utocPath, aesKey, cancellationToken).ConfigureAwait(false)
            : [];

        if (filters.Count == 0 && ownEntries.Count == 0)
        {
            // Nothing to scope the merged view to: unfiltered, it would convert the whole game
            await RetocProcess.ConvertToLegacyAsync(utocPath, output, filters, aesKey, cancellationToken).ConfigureAwait(false);
            return LegacyConversionResult.Alone;
        }

        await RetocProcess.ConvertToLegacyAsync(scope.InputDirectory, output, filters.Count > 0 ? filters : ownEntries, aesKey, cancellationToken).ConfigureAwait(false);

        if (!prune)
            return new LegacyConversionResult(true, ownEntries.Count, [], BaseGameBulkKept: false);
        if (IsPakOutput(output))
            return new LegacyConversionResult(true, ownEntries.Count, [], BaseGameBulkKept: true);
        return new LegacyConversionResult(true, ownEntries.Count, RemoveBulkFilesNotShipped(output, ownEntries), BaseGameBulkKept: false);
    }

    /// <summary>The container file (the .utoc, its .ucas, or a Marvel Rivals container's companion .pak) that a .pak
    /// <paramref name="output"/> would write over, by any name or hard link; null when it reaches none of them, or is a folder.
    /// retoc reads these while it writes the output.</summary>
    /// <exception cref="PathNotPlaceableException">A path cannot be resolved, so whether it reaches a container file is unknown.</exception>
    public static string? ContainerFileReachedBy(string utocPath, string output)
    {
        ArgumentNullException.ThrowIfNull(utocPath);
        ArgumentNullException.ThrowIfNull(output);
        if (!IsPakOutput(output)) return null;
        return ((string[])[utocPath, .. RetocProcess.CompanionsOf(utocPath, writesCompanionPak: true)])
            .FirstOrDefault(own => FinalPaths.SameFile(output, own));
    }

    // uacli and MCP refuse this first, under their own argument names.
    private static void RefuseOwnFile(string utocPath, string output)
    {
        if (ContainerFileReachedBy(utocPath, output) is { } own)
            throw new ArgumentException($"The output is the container's own {Path.GetExtension(own)}, which retoc reads while it writes: {output}. Choose another path.", nameof(output));
    }

    private static bool IsPakOutput(string path) => string.Equals(Path.GetExtension(path), ".pak", StringComparison.OrdinalIgnoreCase);

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
