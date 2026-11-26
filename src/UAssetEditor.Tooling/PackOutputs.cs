using UAssetEditor.Core.AssetSources.IoStore;

namespace UAssetEditor.Tooling;

/// <summary>What a pack may replace or write into, checked the same way by every front end before anything is built.</summary>
internal static class PackOutputs
{
    /// <summary>Refuses a pack of <paramref name="source"/> into <paramref name="output"/> that would replace existing files without
    /// <paramref name="overwrite"/>, put the output inside the source, or (for a .pak written in place) change a file in the source.
    /// A .pak built under another name and moved over the output (<paramref name="pakIsStaged"/>) cannot write into the source, and
    /// neither can an IoStore container, which <see cref="RetocProcess.ConvertToZenAsync"/> always builds that way.</summary>
    public static void Refuse(string source, string output, bool writesCompanionPak, bool overwrite, bool pakIsStaged, PackNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        OutputPaths.RefuseExisting(output, names.Output, overwrite, names.Overwrite);
        OutputPaths.RefuseInside(output, source, names.Output, names.Source);

        var isIoStore = string.Equals(Path.GetExtension(output), ".utoc", StringComparison.OrdinalIgnoreCase);
        if (!isIoStore)
        {
            if (!pakIsStaged) OutputPaths.RefuseWriteInto(output, source, names.Output, names.Source);
            return;
        }

        if (!overwrite && RetocProcess.CompanionsOf(output, writesCompanionPak).FirstOrDefault(File.Exists) is { } existing)
            throw new ArgException($"{existing} already exists. Pass {names.Overwrite} to replace the container's files.");
    }
}

/// <summary>How a front end names the pack's arguments in its messages.</summary>
internal sealed record PackNames(string Source, string Output, string Overwrite);
