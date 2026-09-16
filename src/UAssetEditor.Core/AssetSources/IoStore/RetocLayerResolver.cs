using System.Linq;
using System.Runtime.InteropServices;

namespace UAssetEditor.Core.AssetSources.IoStore;

/// <summary>Which version of an overridden asset a Paks-folder-wide retoc conversion should resolve to.</summary>
public enum RetocLayer
{
    /// <summary>The base game's own content only - the target container's own edits are excluded, even for the entries it overrides.</summary>
    Original,

    /// <summary>The target container's own edits, falling back to the base game for whatever it doesn't itself override.</summary>
    Modded,
}

/// <summary>
/// Resolves the directory retoc's own directory-scan should be pointed at for a given target
/// .utoc and <see cref="RetocLayer"/>. retoc's directory input (see `to-legacy --help`: "directory
/// with multiple .utoc") scans only that directory's own immediate entries - not subfolders - so
/// a mod living under a real Paks folder's "~mods" subfolder (this game's near-universal layout,
/// e.g. "Paks/~mods/&lt;file&gt;.utoc" or "Paks/~mods/&lt;author&gt;/&lt;file&gt;.utoc") is invisible
/// to a scan of "Paks" itself. Pointing retoc straight at the enclosing Paks folder - what this
/// app always did before this existed - therefore silently converts the BASE GAME's own vanilla
/// content for every requested entry whenever that entry's own container sits in a subfolder, not
/// an error, just wrong output: confirmed by converting a real, known-edited mod entry through
/// that path and finding none of its edits in the result. <see cref="RetocLayer.Modded"/> fixes
/// this by hard-linking the target's own .utoc/.ucas alongside the base game's containers in a
/// scratch directory retoc's scan can actually see both in - hard links, not copies, so this costs
/// no extra disk space or meaningful time even against a many-gigabyte base game install.
/// </summary>
public static class RetocLayerResolver
{
    private static readonly string[] ContainerExtensions = [".utoc", ".ucas"];

    /// <summary>
    /// Resolves <paramref name="utocPath"/>'s enclosing Paks folder is found and returns the
    /// directory (or, absent one, <paramref name="utocPath"/> itself, unchanged - there is
    /// nothing to widen against, and <paramref name="layer"/> is meaningless in that case)
    /// retoc should be given as its INPUT. Dispose the returned scope once the retoc call
    /// finishes to clean up any scratch directory it created.
    /// </summary>
    public static RetocInputScope Resolve(string utocPath, RetocLayer layer)
    {
        ArgumentNullException.ThrowIfNull(utocPath);

        var paksFolder = RetocPaksFolderResolver.FindEnclosingPaksFolder(utocPath);
        if (paksFolder == null)
            return RetocInputScope.Unwidened(utocPath);

        if (layer == RetocLayer.Original)
            return RetocInputScope.Widened(paksFolder, temporaryDirectory: null);

        // Must land on the SAME VOLUME as paksFolder - hard links can't cross drives, and
        // Path.GetTempPath() (the system temp folder) is frequently on a different one from a
        // game installed to a non-system drive. Getting this wrong doesn't fail loudly: every
        // link below would just silently fall back to a full copy of each base-game container,
        // turning a near-instant operation into one that copies tens of gigabytes - confirmed by
        // a real run that blew well past a 6-minute timeout before this was caught. A dot-prefixed
        // sibling of "Paks" itself (inside its own parent) keeps it off any drive root, away from
        // Paks' own listing (so it's never itself picked up by a later, unrelated widen), and
        // guaranteed writable whenever Paks itself is.
        var scratchRoot = Path.Combine(Directory.GetParent(paksFolder)!.FullName, ".uae-retoc-temp");
        Directory.CreateDirectory(scratchRoot);
        var scratch = Path.Combine(scratchRoot, "modded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            LinkContainersFrom(paksFolder, scratch);
            LinkContainer(utocPath, scratch);
        }
        catch
        {
            Directory.Delete(scratch, recursive: true);
            throw;
        }

        return RetocInputScope.Widened(scratch, scratch);
    }

    private static void LinkContainersFrom(string sourceFolder, string destFolder)
    {
        foreach (var utoc in Directory.EnumerateFiles(sourceFolder, "*.utoc"))
            LinkContainer(utoc, destFolder);
    }

    private static void LinkContainer(string utocPath, string destFolder)
    {
        var sourceFolder = Path.GetDirectoryName(utocPath)!;
        var baseName = Path.GetFileNameWithoutExtension(utocPath);

        foreach (var extension in ContainerExtensions)
        {
            var source = Path.Combine(sourceFolder, baseName + extension);
            if (!File.Exists(source)) continue;

            var dest = Path.Combine(destFolder, baseName + extension);
            if (File.Exists(dest)) continue; // already linked - e.g. the target itself also appears in the base game's own folder listing

            HardLinkOrCopy(source, dest);
        }
    }

    private static void HardLinkOrCopy(string source, string dest)
    {
        if (!CreateHardLinkW(dest, source, IntPtr.Zero))
            File.Copy(source, dest); // cross-volume, or a filesystem without hard-link support
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}

/// <summary>The directory (or standalone file) to hand retoc as INPUT, and the scratch directory (if any) to delete once the retoc call using it has finished.</summary>
public sealed class RetocInputScope : IDisposable
{
    public string InputDirectory { get; }
    private readonly string? _temporaryDirectory;

    private RetocInputScope(string inputDirectory, string? temporaryDirectory)
    {
        InputDirectory = inputDirectory;
        _temporaryDirectory = temporaryDirectory;
    }

    internal static RetocInputScope Unwidened(string utocPath) => new(utocPath, temporaryDirectory: null);
    internal static RetocInputScope Widened(string directory, string? temporaryDirectory) => new(directory, temporaryDirectory);

    public void Dispose()
    {
        if (_temporaryDirectory is not { } dir || !Directory.Exists(dir)) return;

        Directory.Delete(dir, recursive: true);

        // Also remove the shared ".uae-retoc-temp" wrapper once nothing's left in it, so a
        // Modded-layer conversion doesn't leave a permanent, empty, hidden folder sitting in the
        // user's actual game install forever - safe even under a rare concurrent-conversion race,
        // since deleting an empty directory can't lose anyone's data; a non-empty one (another
        // conversion's subfolder still in flight) is simply left alone.
        var parent = Path.GetDirectoryName(dir);
        if (parent is { } p && Directory.Exists(p) && !Directory.EnumerateFileSystemEntries(p).Any())
        {
            try { Directory.Delete(p); }
            catch (IOException) { /* another conversion just claimed it - fine, leave it */ }
        }
    }
}
