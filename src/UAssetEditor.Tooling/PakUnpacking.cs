namespace UAssetEditor.Tooling;

/// <summary>Checks every front end makes before unpacking a pak into a folder.</summary>
internal static class PakUnpacking
{
    /// <summary>Refuses, before anything is written, when one of <paramref name="entries"/> would replace a file in
    /// <paramref name="folder"/>; <paramref name="overwriteOption"/> names how the caller allows it.</summary>
    public static void RefuseExisting(IEnumerable<string> entries, string folder, string overwriteOption)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.FirstOrDefault(entry => ExistsIn(folder, entry)) is { } existing)
            throw new ArgException($"{existing} already exists in the destination. Pass {overwriteOption} to replace existing files.");
    }

    private static bool ExistsIn(string folder, string entry)
    {
        try
        {
            return File.Exists(Path.GetFullPath(Path.Combine(folder, entry.Replace('/', Path.DirectorySeparatorChar))));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false; // the unpacker records such an entry as failed
        }
    }
}
