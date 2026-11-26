using ModelContextProtocol;
using UAssetEditor.Tooling;

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

    /// <summary>How the pack and convert tools name their arguments in the shared output checks.</summary>
    public static readonly PackNames PackNames = new("sourceFolder", "output", "overwrite=true");

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
