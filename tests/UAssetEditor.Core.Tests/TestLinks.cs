using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UAssetEditor.Core.Tests;

/// <summary>Makes the links a test needs and unlinks them again. Junctions, hard links and 8.3 names need no privilege, unlike
/// symbolic links. A junction must be removed as a link before its folder is deleted, or the delete walks into its target.</summary>
public sealed class TestLinks : IDisposable
{
    private readonly List<string> _junctions = [];

    public void MakeJunction(string link, string target)
    {
        RunMklink("/J", link, target);
        _junctions.Add(link);
    }

    /// <summary>A link to a folder that needs no privilege: a junction on Windows, a symbolic link elsewhere.</summary>
    public void MakeFolderLink(string link, string target)
    {
        if (OperatingSystem.IsWindows()) MakeJunction(link, target);
        else Directory.CreateSymbolicLink(link, target);
    }

    public static void MakeHardLink(string link, string target) => RunMklink("/H", link, target);

    /// <summary>The 8.3 name of an existing path, or the path unchanged when the volume keeps no short names.</summary>
    public static string ShortName(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathNameW(path, buffer, buffer.Length);
        return length is > 0 and < 1024 ? new string(buffer, 0, length) : path;
    }

    public void Dispose()
    {
        foreach (var junction in _junctions.Where(Directory.Exists)) Directory.Delete(junction);
        _junctions.Clear();
    }

    private static void RunMklink(string kind, string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in (string[])["/c", "mklink", kind, link, target]) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"mklink {kind} failed: {output}");
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetShortPathNameW(string longPath, [Out] char[] shortPath, int bufferLength);
}
