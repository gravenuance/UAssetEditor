using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UAssetEditor.Core;

/// <summary>Where a path really lands once links are followed. A lexical comparison of two paths is fooled by
/// symbolic links, junctions, hard links, 8.3 short names and case; every check that a path stays inside a folder or
/// differs from another resolves it here first.</summary>
public static class FinalPaths
{
    private const int MaxLinkHops = 40;

    /// <summary>
    /// The full path with every link, junction, 8.3 short name and letter case in the part that exists resolved to what is on
    /// disk; a tail that does not exist yet (a file about to be created) is appended as given.
    /// </summary>
    public static string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var existing = full;
        var tail = new Stack<string>();
        while (!Path.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent == null) return full; // not even the root exists (a drive that is not there): nothing to resolve
            tail.Push(Path.GetFileName(existing));
            existing = parent;
        }

        var resolved = OperatingSystem.IsWindows() ? FinalPathOfHandle(existing) ?? existing : ResolveLinks(existing);
        while (tail.Count > 0) resolved = Path.Combine(resolved, tail.Pop());
        return resolved;
    }

    /// <summary>True when both names reach the same file: they resolve to one path, or both exist and share a volume and file id (hard links).</summary>
    public static bool SameFile(string first, string second)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(first);
        ArgumentException.ThrowIfNullOrWhiteSpace(second);

        if (string.Equals(Resolve(first), Resolve(second), PathComparison)) return true;
        return OperatingSystem.IsWindows() && File.Exists(first) && File.Exists(second)
            && FileIdOf(first) is { } firstId && FileIdOf(second) is { } secondId && firstId == secondId;
    }

    /// <summary>True when <paramref name="path"/> lies inside <paramref name="folder"/> once both are resolved; the folder itself is not inside.</summary>
    public static bool IsInside(string path, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var root = Path.TrimEndingDirectorySeparator(Resolve(folder)) + Path.DirectorySeparatorChar;
        return Resolve(path).StartsWith(root, PathComparison);
    }

    /// <summary>The comparison the platform's file names follow.</summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Other systems: follow each link part by part, as realpath does. The Windows path above asks the file system instead.
    private static string ResolveLinks(string existing)
    {
        var current = Path.GetPathRoot(existing)!;
        var pending = SplitParts(existing, current);
        var hops = 0;
        while (pending.Count > 0)
        {
            var next = Path.Combine(current, pending[0]);
            pending.RemoveAt(0);
            if (new FileInfo(next).LinkTarget is not { } target)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinkHops) throw new IOException($"Too many levels of links resolving {existing}.");
            var absolute = Path.GetFullPath(target, current);
            current = Path.GetPathRoot(absolute)!;
            pending.InsertRange(0, SplitParts(absolute, current));
        }
        return current;
    }

    private static List<string> SplitParts(string path, string root) =>
        [.. path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)];

    private const uint FileFlagBackupSemantics = 0x02000000; // lets a folder be opened
    private const uint ShareAll = 0x7; // read, write and delete: opening must not disturb anyone using the file
    private const uint OpenExisting = 3;
    private const uint NoAccessNeeded = 0;

    private static SafeFileHandle OpenForQuery(string path) =>
        CreateFileW(path, NoAccessNeeded, ShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);

    private static string? FinalPathOfHandle(string path)
    {
        using var handle = OpenForQuery(path);
        if (handle.IsInvalid) return null;

        var buffer = new char[1024];
        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0) return null;
            if (length < buffer.Length) return StripExtendedPrefix(new string(buffer, 0, (int)length));
            buffer = new char[length + 1];
        }
    }

    private static string StripExtendedPrefix(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
        : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
        : path;

    private static (uint Volume, uint High, uint Low)? FileIdOf(string path)
    {
        using var handle = OpenForQuery(path);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return null;
        return (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] filePath, uint filePathLength, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
