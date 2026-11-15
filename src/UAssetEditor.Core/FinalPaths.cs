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
    /// <remarks>Only DOS (<c>C:\...</c>) and UNC (<c>\\server\share\...</c>) forms come back, with no <c>.</c> or <c>..</c> segment, so two paths compare by their text. A <c>\\?\C:\...</c> input is read as <c>C:\...</c>; any other <c>\\?\</c> or <c>\\.\</c> form is refused.</remarks>
    /// <exception cref="PathNotPlaceableException">The path is refused, with the reason, rather than judged by its name, when the file system could not say where its existing part lands: a link loop or too many links, an offline share, no access, a file in use, a volume with no drive letter, a device or volume name. Only a link whose target is missing and a volume that reports no final paths are placed another way.</exception>
    public static string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return ResolveFull(Normalise(path), OperatingSystem.IsWindows() ? FinalPathOfHandle : null, LongPathOf, new LinkBudget());
    }

    // The entry choke point: a device-prefixed spelling is read as the plain one (so '..' collapses) or refused.
    internal static string Normalise(string path) => Path.GetFullPath(WithoutDevicePrefix(path));

    /// <summary>The path with a <c>\\?\C:\</c>, <c>\\.\C:\</c> or <c>\\?\UNC\</c> prefix turned into the drive or network form;
    /// any other <c>\\?\</c>, <c>\\.\</c> or <c>\??\</c> form (a volume GUID, GLOBALROOT, a device) names no plain path and is refused.</summary>
    private static string WithoutDevicePrefix(string path)
    {
        // Elsewhere a backslash is an ordinary character of a file name.
        if (!OperatingSystem.IsWindows()) return path;
        if (!(path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal)))
            return path;

        var rest = path[4..];
        string plain;
        if (path[2] == '?' && rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) plain = @"\\" + rest[4..];
        else if (rest.Length >= 3 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && rest[2] == '\\') plain = rest;
        else throw PathNotPlaceableException.Unplaceable(path, "it names a device or volume, not a drive or network path");

        // \\?\ takes names literally, but GetFullPath would drop a trailing dot or space and land in another folder.
        if (path[2] != '.' && plain.Split('\\').Any(segment => segment is not ("." or "..") && (segment.EndsWith('.') || segment.EndsWith(' '))))
            throw PathNotPlaceableException.Unplaceable(path, "a name in it ends in a dot or a space, which Windows cannot name");
        return plain;
    }

    // The exit choke point: whatever a branch produced, only a plain drive or network path leaves.
    private static string Gate(string resolved, string source)
    {
        var separator = Path.DirectorySeparatorChar;
        bool plain;
        if (OperatingSystem.IsWindows())
        {
            var drive = resolved.Length >= 3 && char.IsAsciiLetter(resolved[0]) && resolved[1] == ':' && resolved[2] == '\\';
            var network = resolved.Length > 2 && resolved.StartsWith(@"\\", StringComparison.Ordinal) && resolved[2] is not ('?' or '.' or '\\');
            plain = drive || network;
        }
        else
        {
            plain = resolved.StartsWith('/');
        }

        if (plain && !resolved.Split(separator).Any(segment => segment is "." or "..")) return resolved;
        throw PathNotPlaceableException.Unplaceable(source, $"its final form {resolved} is not a plain drive or network path");
    }

    /// <summary>What the file system said about one path: where it lands, or the Win32 error that stopped it saying.</summary>
    internal readonly record struct HandleLookup(string? FinalPath, int Error);

    /// <summary>The error a lookup reports for a path whose handle opened but whose volume has no drive letter, so no DOS name (a made-up code, not a Win32 one).</summary>
    internal const int VolumeHasNoDriveLetter = -1;

    /// <summary>The links one resolution may follow in all. Every link-following pass and the re-placing of its result share
    /// one budget, so a chain that runs across them is bounded too.</summary>
    internal sealed class LinkBudget(int spent = 0)
    {
        private int _spent = spent;

        public void Spend(string path)
        {
            if (++_spent > MaxLinkHops) throw PathNotPlaceableException.LinkLoop(path);
        }
    }

    // A null lookup follows the links part by part (other systems); the Windows lookup asks the file system instead.
    internal static string ResolveFull(string full, Func<string, HandleLookup>? lookup, Func<string, string?> longPathOf, LinkBudget budget)
    {
        var existing = full;
        var tail = new Stack<string>();
        while (!Path.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent == null) return Gate(full, full); // not even the root exists (a drive that is not there): nothing to resolve
            tail.Push(Path.GetFileName(existing));
            existing = parent;
        }

        var resolved = lookup == null ? ResolveLinks(existing, LinkTargetOf, budget) : ResolveExisting(existing, lookup, longPathOf, budget);
        while (tail.Count > 0) resolved = Path.Combine(resolved, tail.Pop());
        return Gate(resolved, full);
    }

    private const string NoDriveLetter = "it is on a volume with no drive letter";

    /// <summary>Where an existing path lands according to <paramref name="lookup"/>. A path the file system cannot place is
    /// refused with the reason, never judged by its name: a link to an offline share or a loop would pass for a path inside
    /// any folder its own name sits in. The exceptions are a link whose target is missing and a volume that cannot report
    /// final paths; a volume with no drive letter has no DOS name and is refused. Every re-entry spends a hop.</summary>
    internal static string ResolveExisting(string existing, Func<string, HandleLookup> lookup, Func<string, string?>? longPathOf = null, LinkBudget? budget = null)
    {
        longPathOf ??= LongPathOf;
        budget ??= new LinkBudget();
        var found = lookup(existing);
        if (found.FinalPath is { } final) return final;
        if (found.Error == VolumeHasNoDriveLetter) throw PathNotPlaceableException.Unplaceable(existing, NoDriveLetter);

        // A handle cannot be opened through a link whose target is missing; following the links by name then still lands
        // where a write through it would. The target's own existing part is placed in turn, so its case and short names match.
        if (found.Error is ErrorFileNotFound or ErrorPathNotFound)
        {
            var followed = ResolveLinks(existing, LinkTargetOf, budget);
            if (string.Equals(followed, existing, PathComparison)) throw PathNotPlaceableException.Unplaceable(existing, Marshal.GetPInvokeErrorMessage(found.Error));
            budget.Spend(existing);
            return ResolveFull(followed, lookup, longPathOf, budget);
        }

        // A RAM disk, a VM shared folder or WebDAV share keeps no final paths: the links are followed by name, a target
        // that leads elsewhere is placed in turn, and the short names of what stays are written out.
        if (found.Error is ErrorInvalidFunction or ErrorNotSupported)
        {
            var followed = ResolveLinks(existing, LinkTargetOf, budget);
            if (string.Equals(followed, existing, PathComparison))
                return longPathOf(followed) ?? throw PathNotPlaceableException.Unplaceable(existing, "its long name cannot be read");
            budget.Spend(existing);
            return ResolveFull(followed, lookup, longPathOf, budget);
        }

        throw found.Error == ErrorCantResolveFilename
            ? PathNotPlaceableException.LinkLoop(existing) // what a link loop reports
            : PathNotPlaceableException.Unplaceable(existing, Marshal.GetPInvokeErrorMessage(found.Error));
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
    internal static string ResolveLinks(string existing, Func<string, string?> linkTargetOf, LinkBudget budget)
    {
        var current = Path.GetPathRoot(existing)!;
        var pending = SplitParts(existing, current);
        while (pending.Count > 0)
        {
            var next = Path.Combine(current, pending[0]);
            pending.RemoveAt(0);
            if (LinkTargetIn(existing, next, linkTargetOf) is not { } target)
            {
                current = next;
                continue;
            }

            budget.Spend(existing);
            var absolute = Path.GetFullPath(WithoutDevicePrefix(target), current);
            current = Path.GetPathRoot(absolute)!;
            pending.InsertRange(0, SplitParts(absolute, current));
        }
        return current;
    }

    private static string? LinkTargetOf(string path) => new FileInfo(path).LinkTarget;

    /// <summary>The path with its 8.3 names written out, or null when the file system cannot say; other systems have none, so the path itself.</summary>
    private static string? LongPathOf(string path)
    {
        return OperatingSystem.IsWindows() ? ReadChars(buffer => GetLongPathNameW(path, buffer, (uint)buffer.Length)) : path;
    }

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
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;
    private const int ErrorCantResolveFilename = 1921;

    private static HandleLookup FinalPathOfHandle(string path)
    {
        using var handle = OpenForQuery(path);
        if (handle.IsInvalid) return new HandleLookup(null, Marshal.GetLastPInvokeError());

        if (ReadChars(buffer => GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0)) is { } final)
            return new HandleLookup(StripExtendedPrefix(final), 0);

        // A handle that opened but has no DOS name: the volume has no drive letter.
        var error = Marshal.GetLastPInvokeError();
        return new HandleLookup(null, error == ErrorPathNotFound ? VolumeHasNoDriveLetter : error);
    }

    // The Windows string calls return the length they need when the buffer is too small, and 0 on failure.
    private static string? ReadChars(Func<char[], uint> call)
    {
        var buffer = new char[1024];
        while (true)
        {
            var length = call(buffer);
            if (length == 0) return null;
            if (length < buffer.Length) return new string(buffer, 0, (int)length);
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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint longPathLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] filePath, uint filePathLength, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
