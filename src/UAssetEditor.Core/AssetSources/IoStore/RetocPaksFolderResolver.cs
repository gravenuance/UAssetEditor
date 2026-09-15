namespace UAssetEditor.Core.AssetSources.IoStore;

/// <summary>
/// Walks up from a single .utoc's containing folder to find its enclosing "Paks" folder - the
/// kind of folder retoc's own `to-legacy --help` documents accepting directly ("Input .utoc or
/// directory with multiple .utoc (e.g. Content/Paks/)"), so retoc can resolve imports into
/// sibling containers a single, standalone .utoc can't see on its own. Confirmed against a real
/// mod: a small overlay skin mod's .utoc only overrides a handful of a character's assets, which
/// import that character's base mesh/skeleton/materials from the *game's own* separate global
/// container - `to-legacy` on the mod file alone fails every single requested asset with an
/// unresolvable-import error, while pointing it at the whole Paks folder instead (same AES key,
/// same -f filter) converts them all with none failing.
/// </summary>
public static class RetocPaksFolderResolver
{
    private const string PaksFolderName = "Paks";

    /// <summary>
    /// Returns the nearest ancestor directory named "Paks" (case-insensitive - matches the
    /// near-universal UE convention of "Content/Paks/", with mods typically living a level or
    /// two deeper under a "~mods" subfolder), starting from <paramref name="utocPath"/>'s own
    /// containing folder. Returns null if none is found (e.g. a .utoc opened from somewhere
    /// outside a real game's Paks tree, or a synthetic/test container) - callers should fall
    /// back to treating <paramref name="utocPath"/> as self-contained in that case, exactly as
    /// before this existed.
    /// </summary>
    public static string? FindEnclosingPaksFolder(string utocPath)
    {
        ArgumentNullException.ThrowIfNull(utocPath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(utocPath)) is { } dir ? new DirectoryInfo(dir) : null;
        while (directory != null)
        {
            if (string.Equals(directory.Name, PaksFolderName, StringComparison.OrdinalIgnoreCase))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }
}
