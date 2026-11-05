using System.Globalization;
using System.Text.RegularExpressions;

namespace UAssetEditor.Core.Games.Rivals;

/// <summary>Mods mounted in one Rivals session but not the other, as the <c>.utoc</c> paths relative to <c>~mods</c>.</summary>
public sealed record RivalsModDiff
{
    public required IReadOnlyList<string> OnlyInFirst { get; init; }

    public required IReadOnlyList<string> OnlyInSecond { get; init; }
}

/// <summary>What matters for diagnosing a Marvel Rivals session, read from its decoded log text.</summary>
public sealed partial record RivalsLogSummary
{
    /// <summary>How many lines before the crash block <see cref="CharacterLoadHeroIds"/> looks at.</summary>
    public const int CharacterLoadWindow = 300;

    /// <summary>The engine version line's value, e.g. <c>5.3.2-4008108+++depot_marvel+S10.5_release</c>; null when the log has none.</summary>
    public string? EngineVersion { get; init; }

    /// <summary>The game build in <see cref="EngineVersion"/> (4008108 in the example above).</summary>
    public int? Build { get; init; }

    /// <summary>Mounted mod containers: <c>.utoc</c> paths relative to <c>~mods</c> as the mount lines print them, once each, in mount order.</summary>
    public required IReadOnlyList<string> Mods { get; init; }

    /// <summary>Whether the log holds a <c>LogWindows: Error: === Critical error: ===</c> block.</summary>
    public bool Crashed { get; init; }

    /// <summary>
    /// The ids in the last <c>hero_id_list=[...]</c> (the bare key: <c>teammates_hero_id_list</c> and the like are other fields)
    /// logged before the crash block, or in the whole log when it did not crash. Empty when there is none.
    /// </summary>
    public required IReadOnlyList<int> LastHeroIdList { get; init; }

    /// <summary>
    /// Hero ids of the <c>&lt;heroId&gt;_CharacterBP</c> actors named in the <see cref="CharacterLoadWindow"/> lines before the
    /// crash block, in order of first appearance. Empty when the log did not crash.
    /// </summary>
    public required IReadOnlyList<int> CharacterLoadHeroIds { get; init; }

    /// <summary>Every line with <c>ObjectSerializationError</c>, <c>Bad name index</c> or <c>Fatal error</c>, without the timestamp prefix.</summary>
    public required IReadOnlyList<string> ErrorLines { get; init; }

    /// <summary>The crash block from <c>=== Critical error: ===</c> through <c>Crash in runnable thread ...</c> (or the end of the log), without the timestamp prefix.</summary>
    public required IReadOnlyList<string> CrashBlock { get; init; }

    /// <summary>Summarises decoded log lines in one pass; keeps only the last <see cref="CharacterLoadWindow"/> lines in memory.</summary>
    public static RivalsLogSummary Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var mods = new List<string>();
        var seenMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var crashBlock = new List<string>();
        var window = new Queue<string>(CharacterLoadWindow + 1);
        string? engineVersion = null;
        int? build = null;
        IReadOnlyList<int> heroIds = [];
        IReadOnlyList<int> characterHeroes = [];
        var crashed = false;
        var inCrashBlock = false;

        foreach (var line in lines)
        {
            if (engineVersion == null && TryFindEngineVersion(line, out var version, out var versionBuild))
            {
                engineVersion = version;
                build = versionBuild;
            }

            if (IsMountLine(line) && ModContainer().Match(line) is { Success: true } mod)
            {
                var path = mod.Groups["path"].Value.Replace('\\', '/');
                if (seenMods.Add(path)) mods.Add(path);
            }

            if (ErrorMarkers.Any(marker => line.Contains(marker, StringComparison.Ordinal)))
                errors.Add(StripPrefix(line));

            if (!crashed && line.Contains(CriticalError, StringComparison.Ordinal))
            {
                crashed = true;
                inCrashBlock = true;
                characterHeroes = HeroIdsOfCharacters(window);
            }

            if (inCrashBlock)
            {
                crashBlock.Add(StripPrefix(line));
                if (line.Contains(CrashInRunnableThread, StringComparison.Ordinal)) inCrashBlock = false;
            }
            else if (!crashed)
            {
                if (HeroIdList().Match(line) is { Success: true } heroList) heroIds = ParseIds(heroList.Groups["ids"].Value);
                window.Enqueue(line);
                if (window.Count > CharacterLoadWindow) window.Dequeue();
            }
        }

        return new RivalsLogSummary
        {
            EngineVersion = engineVersion,
            Build = build,
            Mods = mods,
            Crashed = crashed,
            LastHeroIdList = heroIds,
            CharacterLoadHeroIds = characterHeroes,
            ErrorLines = errors,
            CrashBlock = crashBlock,
        };
    }

    /// <summary>The engine version a log line names, skipping the <c>Compatible Engine Version:</c> line next to it.</summary>
    public static bool TryFindEngineVersion(string text, out string version, out int build)
    {
        ArgumentNullException.ThrowIfNull(text);
        version = "";
        build = 0;
        var match = EngineVersionLine().Match(text);
        if (!match.Success || !int.TryParse(match.Groups["build"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out build)) return false;
        version = match.Groups["version"].Value;
        return true;
    }

    /// <summary>Mods mounted in one summary but not the other (names compared case-insensitively).</summary>
    public static RivalsModDiff DiffMods(RivalsLogSummary first, RivalsLogSummary second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return new RivalsModDiff
        {
            OnlyInFirst = Except(first.Mods, second.Mods),
            OnlyInSecond = Except(second.Mods, first.Mods),
        };
    }

    private static List<string> Except(IReadOnlyList<string> from, IReadOnlyList<string> other)
    {
        var otherSet = new HashSet<string>(other, StringComparer.OrdinalIgnoreCase);
        return [.. from.Where(name => !otherSet.Contains(name))];
    }

    private const string CriticalError = "LogWindows: Error: === Critical error: ===";

    private const string CrashInRunnableThread = "Crash in runnable thread";

    private static readonly string[] ErrorMarkers = ["ObjectSerializationError", "Bad name index", "Fatal error"];

    private static bool IsMountLine(string line) =>
        line.Contains("Mount", StringComparison.Ordinal) && line.Contains(".utoc", StringComparison.OrdinalIgnoreCase);

    private static int[] HeroIdsOfCharacters(IEnumerable<string> lines)
    {
        var ids = new List<int>();
        foreach (var line in lines)
        {
            foreach (Match match in CharacterActor().Matches(line))
            {
                if (int.TryParse(match.Groups["id"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && !ids.Contains(id)) ids.Add(id);
            }
        }
        return [.. ids];
    }

    private static int[] ParseIds(string list) =>
        [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : -1)
            .Where(id => id >= 0)];

    private static string StripPrefix(string line) => LinePrefix().Replace(line, "");

    [GeneratedRegex(@"(?<!Compatible )Engine Version: (?<version>\d+\.\d+\.\d+-(?<build>\d+)\+\+\+\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex EngineVersionLine();

    [GeneratedRegex(@"[/\\]~mods[/\\](?<path>[^'""]+?\.utoc)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ModContainer();

    [GeneratedRegex(@"(?<![A-Za-z_])hero_id_list=\[(?<ids>[0-9, ]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex HeroIdList();

    [GeneratedRegex(@"(?<![0-9])(?<id>[0-9]{3,5})_CharacterBP", RegexOptions.CultureInvariant)]
    private static partial Regex CharacterActor();

    [GeneratedRegex(@"^(?:\[[^\]]*\]){1,3}", RegexOptions.CultureInvariant)]
    private static partial Regex LinePrefix();
}
