using ModelContextProtocol;

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
        if (OperatingSystem.IsWindows() && IsSeparator(text[0]) && text.Length > 1 && IsSeparator(text[1]))
            throw new McpException($"{name} must be a local path, not a network share or device path.");
        if (!Path.IsPathFullyQualified(text)) throw new McpException($"{name} must be an absolute path.");
        return Path.GetFullPath(text);
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

    /// <summary>Refuses <paramref name="candidate"/> when it lies inside <paramref name="folder"/>, so an output cannot end up in what it is built from.</summary>
    public static void RefuseInside(string candidate, string folder, string candidateName, string folderName)
    {
        var root = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        if (candidate.StartsWith(root, PathComparison))
            throw new McpException($"{candidateName} must not be inside {folderName}.");
    }

    /// <summary>Refuses two paths that name the same file.</summary>
    public static void RefuseSame(string first, string second, string firstName, string secondName)
    {
        if (string.Equals(first, second, PathComparison)) throw new McpException($"{firstName} and {secondName} are the same path.");
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
