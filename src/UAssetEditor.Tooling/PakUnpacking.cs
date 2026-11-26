namespace UAssetEditor.Tooling;

/// <summary>Checks every front end makes before unpacking a pak into a folder.</summary>
internal static class PakUnpacking
{
    /// <summary>Refuses, before anything is written, when one of <paramref name="entries"/> would replace a file in
    /// <paramref name="folder"/>; <paramref name="overwriteOption"/> names how the caller allows it.</summary>
    public static void RefuseExisting(IEnumerable<string> entries, string folder, string overwriteOption)
    {
        ArgumentNullException.ThrowIfNull(entries);
        // A new or empty destination holds nothing to replace, so a pak of many entries is not walked for nothing.
        if (!Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any()) return;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        if (entries.FirstOrDefault(entry => ExistsIn(root, entry)) is { } existing)
            throw new ArgException($"{existing} already exists in the destination. Pass {overwriteOption} to replace existing files.");
    }

    // Only a path inside the destination counts: an entry that escapes it is recorded as failed by the unpacker, and checking
    // it here would tell whoever crafted the pak whether a file outside the destination exists.
    private static bool ExistsIn(string root, string entry)
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar)));
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false; // the unpacker records such an entry as failed
        }
    }
}
