using ModelContextProtocol;
using UAssetEditor.Core;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Validation for the file and folder paths the model passes in. Every path must be absolute (a relative one
/// would resolve against whatever folder the client started the server in) and local (opening a network share can send
/// the user's credentials to a host the model chose).</summary>
internal static class ToolPaths
{
    private const int MaxLength = 1024;

    /// <summary>The full form of an absolute local path; it need not exist.</summary>
    public static string Absolute(string? value, string name)
    {
        var text = ToolInput.Required(value, name, MaxLength);
        if (text.Any(char.IsControl)) throw new McpException($"{name} contains control characters.");
        if (OperatingSystem.IsWindows() && IsSeparator(text[0]))
            throw new McpException($"{name} must be a local path on a drive (C:\\...), not a network share or device path.");
        if (!Path.IsPathFullyQualified(text)) throw new McpException($"{name} must be an absolute path.");

        var full = Path.GetFullPath(text);
        // Past the drive's colon, a colon names an alternate data stream, which hides a write beside an existing file.
        if (OperatingSystem.IsWindows() && full.IndexOf(':', 2) >= 0)
            throw new McpException($"{name} must not contain ':' after the drive letter.");
        return full;
    }

    /// <summary>An existing file; <paramref name="extensions"/> (with the dot) restricts what it may be called.</summary>
    public static string ExistingFile(string? value, string name, params string[] extensions)
    {
        var path = Absolute(value, name);
        if (!File.Exists(path)) throw new McpException($"{name} not found: {path}");
        RequireExtension(path, name, extensions);
        return path;
    }

    public static string ExistingFolder(string? value, string name)
    {
        var path = Absolute(value, name);
        return Directory.Exists(path) ? path : throw new McpException($"{name} is not an existing folder: {path}");
    }

    /// <summary>A path that exists as a file or a folder; tells which.</summary>
    public static (string Path, bool IsFolder) ExistingFileOrFolder(string? value, string name, params string[] fileExtensions)
    {
        var path = Absolute(value, name);
        if (Directory.Exists(path)) return (path, true);
        if (!File.Exists(path)) throw new McpException($"{name} not found: {path}");
        RequireExtension(path, name, fileExtensions);
        return (path, false);
    }

    /// <summary>A file to create: its extension is checked, and an existing file is refused unless <paramref name="overwrite"/>.</summary>
    public static string NewFile(string? value, string name, bool overwrite, params string[] extensions)
    {
        var path = Absolute(value, name);
        RequireExtension(path, name, extensions);
        if (Directory.Exists(path)) throw new McpException($"{name} is a folder: {path}");
        return RefuseExisting(path, name, overwrite, File.Exists(path));
    }

    /// <summary>A folder to fill: one that already holds files is refused unless <paramref name="overwrite"/>.</summary>
    public static string NewFolder(string? value, string name, bool overwrite)
    {
        var path = Absolute(value, name);
        if (File.Exists(path)) throw new McpException($"{name} is a file: {path}");
        var holdsFiles = Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        return RefuseExisting(path, name, overwrite, holdsFiles);
    }

    /// <summary>Refuses <paramref name="candidate"/> when it lies inside <paramref name="folder"/>, so an output cannot end up in what it is built from.
    /// Both are resolved first, so a link, junction or 8.3 name into the folder does not get round it.</summary>
    public static void RefuseInside(string candidate, string folder, string candidateName, string folderName)
    {
        if (Placed(() => FinalPaths.IsInside(candidate, folder)))
            throw new McpException($"{candidateName} must not be inside {folderName}.");
    }

    /// <summary>Refuses two paths that name the same file, however they are spelled: through a link or junction, by an 8.3
    /// name, in another letter case, or as a hard link.</summary>
    public static void RefuseSame(string first, string second, string firstName, string secondName)
    {
        if (Placed(() => FinalPaths.SameFile(first, second))) throw new McpException($"{firstName} and {secondName} are the same file.");
    }

    /// <summary><see cref="FinalPaths.Resolve"/> for a tool.</summary>
    public static string Resolve(string path) => Placed(() => FinalPaths.Resolve(path));

    // Wraps only the call itself. An unplaceable path is reported by ToolErrorFilter as it is; any other I/O failure of the resolution (a path too long) becomes a tool error here.
    private static T Placed<T>(Func<T> resolve)
    {
        try
        {
            return resolve();
        }
        catch (IOException ex) when (ex is not PathNotPlaceableException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    private static string RefuseExisting(string path, string name, bool overwrite, bool exists) =>
        exists && !overwrite
            ? throw new McpException($"{name} already exists: {path}. Pass overwrite=true to replace it, or choose another path.")
            : path;

    private static void RequireExtension(string path, string name, string[] extensions)
    {
        if (extensions.Length == 0) return;
        var actual = Path.GetExtension(path);
        if (!extensions.Contains(actual, StringComparer.OrdinalIgnoreCase))
            throw new McpException($"{name} must end in {string.Join(" or ", extensions)}, not '{actual}'.");
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
