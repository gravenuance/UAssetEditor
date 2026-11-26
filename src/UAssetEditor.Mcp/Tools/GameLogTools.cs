using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UAssetEditor.Core.Games.Rivals;
using UAssetEditor.Tooling;

namespace UAssetEditor.Mcp.Tools;

/// <summary>Reads Marvel Rivals logs, which the game encrypts.</summary>
[McpServerToolType]
internal static class GameLogTools
{
    private const int MaxMods = 200;
    private const int MaxErrors = 20;
    private const int MaxCrashLines = 60;
    private const int MaxLineLength = 300;

    [McpServerTool(Name = "game_log_summary", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Decodes a Marvel Rivals log and summarises it: engine build, whether it crashed, both teams' hero ids, the hero ids loaded just before a crash, the mounted mods, the serialization/fatal error lines and the crash block. Start here when the game crashes. With compareWith (a log of a session that worked) it also lists the mods mounted in only one of the two. Only the summary is returned, never the full text, which holds player ids." + Untrusted.Note)]
    public static async Task<GameLogResult> GameLogSummaryAsync(
        [Description("Absolute path of the .log; omit for the newest one in %LOCALAPPDATA%\\Marvel\\Saved\\Logs.")] string? log = null,
        [Description("Absolute path of a second .log to compare the mounted mods with.")] string? compareWith = null,
        CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var path = ToolInput.Optional(log, nameof(log)) is { } given ? ToolPaths.ExistingFile(given, nameof(log), ".log") : NewestLog(notes);
        var other = ToolInput.Optional(compareWith, nameof(compareWith)) is { } second ? ToolPaths.ExistingFile(second, nameof(compareWith), ".log") : null;

        return await Task.Run(() =>
        {
            var (summary, decoded) = GameLogs.Summarise(path);
            AddDecodeNotes(notes, path, decoded);

            ModDiffDto? diff = null;
            if (other != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (otherSummary, otherDecoded) = GameLogs.Summarise(other);
                AddDecodeNotes(notes, other, otherDecoded);
                var mods = RivalsLogSummary.DiffMods(summary, otherSummary);
                diff = new ModDiffDto(Lines(mods.OnlyInFirst, MaxMods), Lines(mods.OnlyInSecond, MaxMods));
            }

            if (summary.Mods.Count > MaxMods) notes.Add($"Showing the first {MaxMods} of {summary.Mods.Count} mods.");
            if (summary.ErrorLines.Count > MaxErrors) notes.Add($"Showing the first {MaxErrors} of {summary.ErrorLines.Count} error lines.");
            if (summary.CrashBlock.Count > MaxCrashLines) notes.Add($"Showing the first {MaxCrashLines} of {summary.CrashBlock.Count} crash lines.");

            return new GameLogResult(
                path,
                summary.EngineVersion is { } engine ? Untrusted.Clean(engine, 100) : null,
                summary.Build,
                summary.Crashed,
                summary.AllyHeroIds,
                summary.EnemyHeroIds,
                summary.CharacterLoadHeroIds,
                Lines(summary.Mods, MaxMods),
                Lines(summary.ErrorLines, MaxErrors),
                Lines(summary.CrashBlock, MaxCrashLines),
                diff,
                notes);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string NewestLog(List<string> notes)
    {
        var newest = RivalsLogs.Newest(RivalsLogs.DefaultDirectory)
            ?? throw new McpException($"No *.log in {RivalsLogs.DefaultDirectory}; pass the log's path.");
        notes.Add($"Read the newest log: {newest}");
        return newest;
    }

    private static void AddDecodeNotes(List<string> notes, string path, RivalsLogDecodeResult decoded)
    {
        var name = Untrusted.Clean(Path.GetFileName(path), 100);
        foreach (var problem in decoded.Problems()) notes.Add($"{name}: {problem}");
    }

    private static List<string> Lines(IReadOnlyList<string> source, int max) => [.. source.Take(max).Select(line => Untrusted.Clean(line, MaxLineLength))];
}
