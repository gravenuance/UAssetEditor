namespace UAssetEditor.Core.AssetSources.IoStore;

/// <summary>
/// Works out the directory retoc's `to-zen` actually needs to be pointed at, and the project
/// name from a pak's mount point - see <see cref="Resolve"/>'s own doc comment for the real-world
/// bug this exists to route around (confirmed against retoc's own source and reproduced against
/// the real vendored retoc.exe): retoc's own package-path derivation for older container header
/// versions (UE4.27 and earlier) requires the walked-directory-relative path of every asset to
/// start with a project-name segment (e.g. "SB/Content/..."), and fails outright
/// ("Failed to get Package Path from Content/...") on a folder that's already rooted at Content/
/// itself with no such segment.
/// </summary>
public static class RetocDirectoryInputResolver
{
    /// <summary>
    /// A UE pak/project root always has at least one of these directly inside it - used to tell
    /// "this folder already IS the project root" apart from "this folder already wraps the
    /// project root one level down" (e.g. a temp folder this app extracted a pak's entries
    /// into). Content is the near-universal one; the others are checked too since a folder can
    /// legitimately lack Content (per <see cref="PackFolderViewModel.OnSourceFolderChanged"/>'s
    /// own remarks on why no single one of these is more authoritative than another).
    /// </summary>
    private static readonly string[] ProjectRootMarkers = ["Content", "Config", "Plugins", "Movies"];

    /// <summary>
    /// Returns the directory to hand retoc's `to-zen` as its INPUT so its own directory walk
    /// reproduces "&lt;ProjectName&gt;/Content/..." relative paths, which is what its
    /// package-path derivation needs for UE4.27-and-earlier containers. If
    /// <paramref name="candidateFolder"/> already looks like the project root itself (has
    /// Content/Config/Plugins/Movies directly inside it - the shape of a folder a user picked by
    /// hand, e.g. "...\SB"), its parent is returned instead, since walking the parent is what
    /// naturally includes the project-name segment retoc needs. If it instead already wraps a
    /// project-root folder one level down (the shape this app itself produces when unpacking a
    /// pak into a project-named subfolder), it's returned unchanged. Returns null if
    /// <paramref name="candidateFolder"/> has no parent to ascend to (e.g. a drive root) and
    /// needed one.
    /// </summary>
    public static string? Resolve(string candidateFolder)
    {
        ArgumentNullException.ThrowIfNull(candidateFolder);

        var trimmed = candidateFolder.TrimEnd('\\', '/');
        if (!LooksLikeProjectRoot(trimmed)) return trimmed;

        return Directory.GetParent(trimmed)?.FullName;
    }

    private static bool LooksLikeProjectRoot(string folder) =>
        ProjectRootMarkers.Any(marker => Directory.Exists(Path.Combine(folder, marker)));

    /// <summary>
    /// Pulls the project-name segment out of a pak's mount point (e.g. "../../../SB/" -&gt;
    /// "SB") - the same segment retoc's to-zen needs as the first path component after its own
    /// implicit "../../../" prefix (see <see cref="Resolve"/>). Returns null if the mount point
    /// has no segment after its leading "../" run (e.g. just "../../../").
    /// </summary>
    public static string? ExtractProjectName(string mountPoint)
    {
        ArgumentNullException.ThrowIfNull(mountPoint);

        var parts = mountPoint.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var afterDotDot = parts.SkipWhile(p => p == "..").ToList();
        return afterDotDot.Count > 0 ? afterDotDot[0] : null;
    }
}
