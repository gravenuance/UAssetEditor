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
    /// <exception cref="PathNotPlaceableException">A link in the existing part loops or leads somewhere the file system cannot place (an offline share): the path is refused rather than judged by its name.</exception>
    public static string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return ResolveFull(Path.GetFullPath(path), OperatingSystem.IsWindows() ? FinalPathOfHandle : null);
    }

    // A null lookup follows the links part by part (other systems); the Windows lookup asks the file system instead.
    private static string ResolveFull(string full, Func<string, HandleLookup>? lookup)
    {
        var existing = full;
        var tail = new Stack<string>();
        while (!Path.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent == null) return full; // not even the root exists (a drive that is not there): nothing to resolve
            tail.Push(Path.GetFileName(existing));
            existing = parent;
        }

        var resolved = lookup == null ? ResolveLinks(existing, LinkTargetOf) : ResolveExisting(existing, lookup);
        while (tail.Count > 0) resolved = Path.Combine(resolved, tail.Pop());
        return resolved;
    }

    /// <summary>What the file system said about one path: where it lands, or the Win32 error that stopped it saying.</summary>
    internal readonly record struct HandleLookup(string? FinalPath, int Error);

    /// <summary>Where an existing path lands according to <paramref name="lookup"/>. When the file system cannot say and a
    /// link is in the path, the path is refused rather than judged by its name: a link to an offline share or a loop would
    /// pass for a path inside any folder its own name sits in.</summary>
    internal static string ResolveExisting(string existing, Func<string, HandleLookup> lookup)
    {
        var found = lookup(existing);
        if (found.FinalPath is { } final) return final;

        // A handle cannot be opened through a link whose target is missing; following the links by name then still lands
        // where a write through it would. The target's own existing part is placed in turn, so its case and short names match.
        if (found.Error is ErrorFileNotFound or ErrorPathNotFound)
        {
            var followed = ResolveLinks(existing, LinkTargetOf);
            return string.Equals(followed, existing, PathComparison) ? followed : ResolveFull(followed, lookup);
        }

        // No link, so the path cannot lead anywhere but where it reads (a RAM disk, a locked file): the real write reports its own error.
        if (!HasLinkInChain(existing)) return Path.GetFullPath(existing);

        throw found.Error == ErrorCantResolveFilename
            ? PathNotPlaceableException.LinkLoop(existing) // what a link loop reports
            : PathNotPlaceableException.Unplaceable(existing, Marshal.GetPInvokeErrorMessage(found.Error));
    }

    /// <summary>True when <paramref name="existing"/> or a folder above it is a link, junction or other reparse point; one whose attributes cannot be read counts as one.</summary>
    private static bool HasLinkInChain(string existing)
    {
        for (var current = existing; Path.GetDirectoryName(current) is { } parent; current = parent)
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }
        return false;
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

        // A root such as C:\ keeps its separator when trimmed, so one is added only where it is missing.
        var root = Path.TrimEndingDirectorySeparator(Resolve(folder));
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
        return Resolve(path).StartsWith(root, PathComparison);
    }

    /// <summary>The comparison the platform's file names follow.</summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Other systems: follow each link part by part, as realpath does. The Windows path above asks the file system instead.
    internal static string ResolveLinks(string existing, Func<string, string?> linkTargetOf)
    {
        var current = Path.GetPathRoot(existing)!;
        var pending = SplitParts(existing, current);
        var hops = 0;
        while (pending.Count > 0)
        {
            var next = Path.Combine(current, pending[0]);
            pending.RemoveAt(0);
            if (LinkTargetIn(existing, next, linkTargetOf) is not { } target)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinkHops) throw PathNotPlaceableException.LinkLoop(existing);
            var absolute = Path.GetFullPath(target, current);
            current = Path.GetPathRoot(absolute)!;
            pending.InsertRange(0, SplitParts(absolute, current));
        }
        return current;
    }

    private static string? LinkTargetOf(string path) => new FileInfo(path).LinkTarget;

    // Reading a link can fail for want of access (Unix) or on a damaged one; either way the path is refused, as a tool error.
    private static string? LinkTargetIn(string existing, string part, Func<string, string?> linkTargetOf)
    {
        try
        {
            return linkTargetOf(part);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PathNotPlaceableException.Unplaceable(existing, $"the link {part} cannot be read: {ex.Message}", ex);
        }
    }

    private static List<string> SplitParts(string path, string root) =>
        [.. path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)];

    private const uint FileFlagBackupSemantics = 0x02000000; // lets a folder be opened
    private const uint ShareAll = 0x7; // read, write and delete: opening must not disturb anyone using the file
    private const uint OpenExisting = 3;
    private const uint NoAccessNeeded = 0;

    private static SafeFileHandle OpenForQuery(string path) =>
        CreateFileW(path, NoAccessNeeded, ShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorCantResolveFilename = 1921;

    private static HandleLookup FinalPathOfHandle(string path)
    {
        using var handle = OpenForQuery(path);
        if (handle.IsInvalid) return new HandleLookup(null, Marshal.GetLastPInvokeError());

        var buffer = new char[1024];
        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0) return new HandleLookup(null, Marshal.GetLastPInvokeError());
            if (length < buffer.Length) return new HandleLookup(StripExtendedPrefix(new string(buffer, 0, (int)length)), 0);
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
        // FILETIMEs are two DWORDs, 4-byte aligned: a long here would pad the struct and shift every field after it.
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
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
