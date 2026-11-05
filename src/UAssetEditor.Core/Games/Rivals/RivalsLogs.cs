using System.Text;

namespace UAssetEditor.Core.Games.Rivals;

/// <summary>Where Marvel Rivals writes its logs, and what they say about the installed game.</summary>
public static class RivalsLogs
{
    /// <summary><c>%LOCALAPPDATA%\Marvel\Saved\Logs</c>.</summary>
    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Marvel", "Saved", "Logs");

    /// <summary>The most recently written <c>*.log</c> in <paramref name="directory"/>, or null when there is none.</summary>
    public static string? Newest(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateFiles(directory, "*.log")
            .Where(path => path.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            .MaxBy(File.GetLastWriteTimeUtc);
    }

    /// <summary>Reads a log in full. The game may still have it open for writing, so it is opened with sharing.</summary>
    public static byte[] ReadAll(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// The game build the newest log in <paramref name="directory"/> was written by, decoding only as far as its engine version
    /// line. Null when there is no log, it cannot be read or decoded, or it has no such line: a stale-usmap check must never
    /// fail a command, so every such case reads as "unknown".
    /// </summary>
    public static int? InstalledBuild(string directory)
    {
        try
        {
            if (Newest(directory) is not { } log) return null;

            int? build = null;
            RivalsLogDecoder.Decode(ReadAll(log), chunk =>
            {
                if (!RivalsLogSummary.TryFindEngineVersion(Encoding.UTF8.GetString(chunk.Span), out _, out var found)) return true;
                build = found;
                return false;
            });
            return build;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }
}
