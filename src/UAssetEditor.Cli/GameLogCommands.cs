using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UAssetEditor.Core.Games.Rivals;

namespace UAssetEditor.Cli;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RivalsLogSummary))]
[JsonSerializable(typeof(RivalsModDiff))]
internal sealed partial class GameLogJsonContext : JsonSerializerContext;

/// <summary>The <c>game-log</c> verb: decode, summarise and compare Marvel Rivals logs.</summary>
internal static class GameLogCommands
{
    // The summary lists every serialization/fatal line in --json; the text form stops here so one bad session does not flood a terminal.
    private const int MaxErrorLinesInText = 20;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static int GameLog(ArgReader args)
    {
        var summary = args.Switch("summary");
        var json = args.Switch("json");
        var diffPath = RequireValue(args, "diff");
        var outPath = RequireValue(args, "out");
        if (summary && diffPath != null) throw new ArgException("--summary and --diff are separate reports; pick one.");
        if (json && !summary && diffPath == null) throw new ArgException("--json applies to --summary and --diff.");

        var logPath = ResolveLog(args.Positional(0));
        if (diffPath != null) return Diff(logPath, diffPath, json, outPath);
        if (summary) return Summary(logPath, json, outPath);
        return Decode(logPath, outPath);
    }

    private static int Decode(string logPath, string? outPath)
    {
        var blob = RivalsLogs.ReadAll(logPath);
        if (outPath != null)
        {
            using var file = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
            Report(logPath, DecodeInto(logPath, blob, file));
            return 0;
        }

        // Bytes, not text: the output must match the log's own bytes exactly, whatever the console encoding.
        Console.Out.Flush();
        using var stdout = new BufferedStream(Console.OpenStandardOutput(), 1 << 16);
        var result = DecodeInto(logPath, blob, stdout);
        stdout.Flush();
        Report(logPath, result);
        return 0;
    }

    private static int Summary(string logPath, bool json, string? outPath)
    {
        var summary = Summarise(logPath);
        WriteText(outPath, output =>
        {
            if (json) output.WriteLine(JsonSerializer.Serialize(summary, GameLogJsonContext.Default.RivalsLogSummary));
            else WriteSummary(output, logPath, summary);
        });
        return 0;
    }

    private static int Diff(string logPath, string otherPath, bool json, string? outPath)
    {
        if (!File.Exists(otherPath)) throw new ArgException($"File not found: {otherPath}");
        var diff = RivalsLogSummary.DiffMods(Summarise(logPath), Summarise(otherPath));

        WriteText(outPath, output =>
        {
            if (json)
            {
                output.WriteLine(JsonSerializer.Serialize(diff, GameLogJsonContext.Default.RivalsModDiff));
                return;
            }
            foreach (var mod in diff.OnlyInFirst) output.WriteLine($"- {mod}");
            foreach (var mod in diff.OnlyInSecond) output.WriteLine($"+ {mod}");
        });
        if (!json && diff.OnlyInFirst.Count == 0 && diff.OnlyInSecond.Count == 0) Console.Error.WriteLine("Both sessions mounted the same mods.");
        return 0;
    }

    private static RivalsLogSummary Summarise(string logPath)
    {
        using var decoded = new MemoryStream();
        Report(logPath, DecodeInto(logPath, RivalsLogs.ReadAll(logPath), decoded));
        decoded.Position = 0;
        return RivalsLogSummary.Parse(ReadLines(decoded));
    }

    private static IEnumerable<string> ReadLines(Stream stream)
    {
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static RivalsLogDecodeResult DecodeInto(string logPath, byte[] blob, Stream output)
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

    // Diagnostics go to stderr so the decoded text on stdout stays clean.
    private static void Report(string logPath, RivalsLogDecodeResult result)
    {
        var name = Path.GetFileName(logPath);
        Console.Error.WriteLine($"{name}: {result.Records} records");
        if (result.InflateFailures > 0)
            Console.Error.WriteLine($"Warning: {name}: {result.InflateFailures} record(s) failed to inflate; their text is cut short or missing.");
        if (result.StoppedAtOffset is { } offset)
            Console.Error.WriteLine($"Warning: {name}: stopped at 0x{offset:x}, which is not a whole record (truncated or damaged log); the rest was not decoded.");
    }

    private static void WriteSummary(TextWriter output, string logPath, RivalsLogSummary summary)
    {
        output.WriteLine($"log: {logPath}");
        output.WriteLine($"engine: {summary.EngineVersion ?? "unknown"}");
        output.WriteLine($"build: {summary.Build?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
        output.WriteLine($"crashed: {(summary.Crashed ? "yes" : "no")}");
        output.WriteLine($"allies: {string.Join(", ", summary.AllyHeroIds)}");
        output.WriteLine($"enemies: {string.Join(", ", summary.EnemyHeroIds)}");
        if (summary.Crashed)
            output.WriteLine($"character-loads-before-crash: {string.Join(", ", summary.CharacterLoadHeroIds)}");
        foreach (var mod in summary.Mods) output.WriteLine($"mod: {mod}");
        foreach (var line in summary.ErrorLines.Take(MaxErrorLinesInText)) output.WriteLine($"error: {line}");
        if (summary.ErrorLines.Count > MaxErrorLinesInText)
            output.WriteLine($"errors-omitted: {summary.ErrorLines.Count - MaxErrorLinesInText} (use --json for all)");
        foreach (var line in summary.CrashBlock) output.WriteLine($"crash: {line}");
    }

    private static string ResolveLog(string? given)
    {
        if (given != null)
            return File.Exists(given) ? given : throw new ArgException($"File not found: {given}");

        var newest = RivalsLogs.Newest(RivalsLogs.DefaultDirectory)
            ?? throw new ArgException($"No *.log in {RivalsLogs.DefaultDirectory}; pass the log's path.");
        Console.Error.WriteLine($"Reading the newest log: {newest}");
        return newest;
    }

    private static string? RequireValue(ArgReader args, string name) =>
        args.Flag(name) ? throw new ArgException($"--{name} needs a value.") : args.Option(name);

    // To the file when --out was given, else to stdout (Console.Out is not ours to close).
    private static void WriteText(string? outPath, Action<TextWriter> write)
    {
        if (outPath == null)
        {
            write(Console.Out);
            return;
        }
        using var file = new StreamWriter(outPath, append: false, Utf8);
        write(file);
    }
}
