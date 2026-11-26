using UAssetEditor.Core.AssetSources.IoStore;

namespace UAssetEditor.Tooling;

/// <summary>How a front end names the to-legacy arguments in its messages.</summary>
internal sealed record LegacyNames(string Utoc, string Output, string Layer, string Paks, string NoPaksResolve, string Overwrite);

/// <summary>Imports resolve through a Paks folder: <see cref="PaksFolder"/>, or the one enclosing the container when null.</summary>
internal sealed record LegacyResolve(RetocLayer Layer, string? PaksFolder);

/// <summary>What a conversion wrote: how its imports resolved (null when they did not go through a Paks folder), and the
/// base-game bulk files it removed because the container does not ship them.</summary>
internal sealed record LegacyOutcome(string Output, string? Resolved, IReadOnlyList<string> RemovedBulkFiles)
{
    /// <summary>The outcome as log lines, naming at most <paramref name="maxRemoved"/> removed files and counting the rest.</summary>
    public IEnumerable<string> Lines(int maxRemoved = int.MaxValue)
    {
        if (Resolved != null) yield return Resolved;
        foreach (var removed in RemovedBulkFiles.Take(maxRemoved))
            yield return $"Removed base-game bulk the container does not ship: {removed}";
        if (RemovedBulkFiles.Count > maxRemoved)
            yield return $"... and {RemovedBulkFiles.Count - maxRemoved} more base-game bulk files removed.";
        yield return $"Converted to {Output}";
    }
}

/// <summary>Converts an IoStore container to legacy format through retoc, with the same checks and messages for every front end.</summary>
internal static class LegacyConversion
{
    /// <summary>An output with the .pak extension is written as a pak, the same test <see cref="IoStoreLegacyConverter"/> makes;
    /// anything else is a folder.</summary>
    public static bool WritesPak(string output) =>
        string.Equals(Path.GetExtension(output), ".pak", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How imports resolve, or null for the container on its own; a layer or Paks folder with <paramref name="noPaksResolve"/>
    /// is refused, since both only mean something when resolving through a Paks folder.
    /// </summary>
    public static LegacyResolve? Resolve(bool noPaksResolve, RetocLayer? layer, string? paksFolder, LegacyNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (!noPaksResolve) return new LegacyResolve(layer ?? RetocLayer.Modded, paksFolder);
        if (layer != null || paksFolder != null)
            throw new ArgException($"{names.Layer} and {names.Paks} choose how imports resolve through a Paks folder, so they cannot be combined with {names.NoPaksResolve}.");
        return null;
    }

    /// <summary>
    /// Refuses an output that is one of the container's own files (retoc reads them while it writes), and, without
    /// <paramref name="overwrite"/>, a .pak that exists or a folder that already holds files.
    /// </summary>
    public static void RefuseOutput(string utoc, string output, bool overwrite, LegacyNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        bool exists;
        if (WritesPak(output))
        {
            if (Directory.Exists(output)) throw new ArgException($"{names.Output} is a folder: {output}");
            // A Marvel Rivals container's own files include a companion .pak.
            foreach (var containerFile in (string[])[utoc, .. PackOutputs.CompanionsOf(utoc, writesCompanionPak: true)])
                OutputPaths.RefuseSame(output, containerFile, names.Output, $"the {names.Utoc}'s {Path.GetExtension(containerFile)}");
            exists = File.Exists(output);
        }
        else
        {
            if (File.Exists(output)) throw new ArgException($"{names.Output} is a file: {output}");
            exists = Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any();
        }

        if (exists && !overwrite)
            throw new ArgException($"{names.Output} already exists: {output}. Pass {names.Overwrite} to replace it, or choose another path.");
    }

    /// <summary>
    /// Converts <paramref name="utoc"/>'s entries (all of them when <paramref name="filters"/> is empty) into <paramref name="output"/>,
    /// which the caller has checked with <see cref="RefuseOutput"/>. Each warning goes to <paramref name="warn"/> when it is known,
    /// so one about how imports resolve is out before retoc starts, and still out when it fails.
    /// </summary>
    public static async Task<LegacyOutcome> RunAsync(
        string utoc,
        string output,
        IReadOnlyList<string> filters,
        byte[]? aesKey,
        LegacyResolve? resolve,
        LegacyNames names,
        Action<string> warn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(warn);
        Directory.CreateDirectory(WritesPak(output) ? Path.GetDirectoryName(Path.GetFullPath(output))! : output);

        if (resolve == null)
        {
            await RetocProcess.ConvertToLegacyAsync(utoc, output, filters, aesKey, cancellationToken).ConfigureAwait(false);
            return new LegacyOutcome(output, null, []);
        }

        if (resolve.PaksFolder == null && RetocPaksFolderResolver.FindEnclosingPaksFolder(utoc) == null)
            warn($"No enclosing Paks folder, so imports into the game can't resolve; pass {names.Paks} for a container outside the game's Paks folder.");

        var result = await IoStoreLegacyConverter.ConvertAsync(utoc, output, resolve.Layer, filters, aesKey, resolve.PaksFolder, cancellationToken).ConfigureAwait(false);
        if (result.BaseGameBulkKept)
            warn("A .pak output can't be pruned, so it may hold base-game bulk files the container doesn't ship; convert to a folder to drop them.");
        var scoped = result.OwnEntryCount > 0 ? $", {result.OwnEntryCount} entries of the container" : "";
        var resolved = result.ResolvedThroughPaksFolder
            ? $"Resolved imports via {resolve.PaksFolder ?? "the enclosing Paks folder"} ({resolve.Layer} layer{scoped})."
            : null;
        return new LegacyOutcome(output, resolved, result.RemovedBaseGameBulkFiles);
    }
}
