using System.Text;
using UAssetEditor.Core.Games.Rivals;

namespace UAssetEditor.Tooling;

/// <summary>Reads Marvel Rivals logs into <see cref="RivalsLogSummary"/> values.</summary>
internal static class GameLogs
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Decodes the log at <paramref name="logPath"/> and summarises it; a log that is not a decodable Rivals log is an <see cref="ArgException"/>.</summary>
    public static (RivalsLogSummary Summary, RivalsLogDecodeResult Decoded) Summarise(string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);

        using var decoded = new MemoryStream();
        var result = DecodeInto(logPath, Read(logPath), decoded);
        decoded.Position = 0;
        return (RivalsLogSummary.Parse(ReadLines(decoded)), result);
    }

    /// <summary>The log's bytes; a file over <see cref="RivalsLogs.MaxLogBytes"/> is an <see cref="ArgException"/>.</summary>
    public static byte[] Read(string logPath)
    {
        try
        {
            return RivalsLogs.ReadAll(logPath);
        }
        catch (InvalidDataException ex)
        {
            throw new ArgException(ex.Message, ex);
        }
    }

    public static RivalsLogDecodeResult DecodeInto(string logPath, byte[] blob, Stream output)
    {
        try
        {
            return RivalsLogDecoder.Decode(blob, output);
        }
        catch (InvalidDataException ex)
        {
            throw new ArgException($"{logPath}: {ex.Message}", ex);
        }
    }

    private static IEnumerable<string> ReadLines(Stream stream)
    {
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (reader.ReadLine() is { } line) yield return line;
    }
}
