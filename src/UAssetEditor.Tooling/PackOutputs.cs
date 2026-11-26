namespace UAssetEditor.Tooling;

/// <summary>What a pack may replace or write into, checked the same way by every front end before anything is built.</summary>
internal static class PackOutputs
{
    /// <summary>The .ucas beside a .utoc is part of the container, and so is the companion .pak a Marvel Rivals pack writes.</summary>
    public static string[] CompanionsOf(string utoc, bool writesCompanionPak) =>
        [.. (writesCompanionPak ? (string[])[".ucas", ".pak"] : [".ucas"]).Select(extension => Path.ChangeExtension(utoc, extension))];

    /// <summary>Refuses a pack of <paramref name="source"/> into <paramref name="output"/> that would replace existing files without
    /// <paramref name="overwrite"/>, put the output inside the source, or (for a file written in place) change a file in the source.
    /// A .pak built under another name and moved over the output (<paramref name="pakIsStaged"/>) cannot write into the source.</summary>
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

        var companions = CompanionsOf(output, writesCompanionPak);
        if (!overwrite && companions.FirstOrDefault(File.Exists) is { } existing)
            throw new ArgException($"{existing} already exists. Pass {names.Overwrite} to replace the container's files.");

        // retoc writes the container's files in place, so a hard link among them would write into the source.
        OutputPaths.RefuseWriteInto(output, source, names.Output, names.Source);
        foreach (var companion in companions)
            OutputPaths.RefuseWriteInto(companion, source, $"{names.Output}'s {Path.GetExtension(companion)}", names.Source);
    }
}

/// <summary>How a front end names the pack's arguments in its messages.</summary>
internal sealed record PackNames(string Source, string Output, string Overwrite);
