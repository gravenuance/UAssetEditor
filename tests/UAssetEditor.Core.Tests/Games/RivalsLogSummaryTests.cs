using UAssetEditor.Core.Games.Rivals;

namespace UAssetEditor.Core.Tests.Games;

public class RivalsLogSummaryTests
{
    private const string P = "[2026.11.05-17.01.08:001 +0100][  1][Bid:abc_1]";

    private static readonly string[] CrashingLog =
    [
        "SharedMemoryLog Init Success",
        $"{P}LogInit: Display: Engine Version: 5.3.2-4008108+++depot_marvel+S10.5_release",
        $"{P}LogInit: Display: Compatible Engine Version: 5.3.2-3998123+++depot_marvel+S10.5_release",
        "LogPakFile: Display: Found Pak file ../../../Marvel/Content/Paks/~mods/zA_9999999_P.pak attempting to mount.",
        "LogIoDispatcher: Display: Reading toc: ../../../Marvel/Content/Paks/~mods/zA_9999999_P.utoc",
        "LogIoDispatcher: Display: Mounting container '../../../Marvel/Content/Paks/~mods/zA_9999999_P.utoc' in location slot 0",
        "LogPakFile: Display: Mounted IoStore container \"../../../Marvel/Content/Paks/~mods/zA_9999999_P.utoc\"",
        "LogPakFile: Display: Mounted IoStore container \"../../../Marvel/Content/Paks/~mods/Sub/zB_9999999_P.utoc\"",
        "LogIoDispatcher: Display: Mounting container '../../../Marvel/Content/Paks/global.utoc' in location slot 0",
        $"{P}LogHeroBonds: Display: [BondDiag] _update_bond_data is_allies=False local_side=0 players=12 hero_id_list=[1042, 1048] bond_id_list=[1]",
        $"{P}LogHeroBonds: Display: [BondDiag] _update_bond_data is_allies=True local_side=0 players=12 hero_id_list=[1042, 1060] teammates_hero_id_list=[7, 8]",
        $"{P}LogHeroBonds: Display: [BondDiag] build ENTER is_allies=True hero_id_list=[1032, 1050, 1060] teammates_hero_id_list=[1050, 1060]",
        $"{P}LogSelectHeroUI: Display: grouping hero_id_list=[1011, 1014] final_hero_id_list=[9, 9] mixed_hero_id_list=[9]",
        $"{P}LogCharacter: Display: 1048_CharacterBP_C_2147465296 hero ability assets load completed!",
        $"{P}LogUObjectGlobals: Error: ObjectSerializationError: Bad name index 12345/6",
        $"{P}LogCharacter: Display: 1032_CharacterBP_C_2147448542 hero sculpt assets load completed! 1048_CharacterBP_C_1",
        $"{P}LogThreadingWindows: Error: Runnable thread FAsyncLoadingThread crashed.",
        $"{P}LogWindows: Error: === Critical error: ===",
        $"{P}LogWindows: Error: Fatal error!",
        $"{P}LogWindows: Error: Unhandled Exception: EXCEPTION_ACCESS_VIOLATION writing address 0x0000",
        $"{P}LogWindows: Error: [Callstack] 0x00007ff6 Marvel-Win64-Shipping.exe!UnknownFunction []",
        $"{P}LogWindows: Error: Crash in runnable thread FAsyncLoadingThread",
        $"{P}LogHeroBonds: Display: [BondDiag] is_allies=True hero_id_list=[1, 2] after the crash",
    ];

    [Fact]
    public void Parse_ReadsEngineBuild_FromTheEngineVersionLineNotTheCompatibleOne()
    {
        var summary = RivalsLogSummary.Parse(CrashingLog);

        Assert.Equal("5.3.2-4008108+++depot_marvel+S10.5_release", summary.EngineVersion);
        Assert.Equal(4008108, summary.Build);
    }

    [Fact]
    public void Parse_ListsMountedModsOnce_RelativeToModsAndSkipsNonMountLines()
    {
        var summary = RivalsLogSummary.Parse(CrashingLog);

        Assert.Equal(["zA_9999999_P.utoc", "Sub/zB_9999999_P.utoc"], summary.Mods);
    }

    [Fact]
    public void Parse_FindsTheCrash_AndTheBlockUpToTheRunnableThreadLine()
    {
        var summary = RivalsLogSummary.Parse(CrashingLog);

        Assert.True(summary.Crashed);
        Assert.Equal(
        [
            "LogWindows: Error: === Critical error: ===",
            "LogWindows: Error: Fatal error!",
            "LogWindows: Error: Unhandled Exception: EXCEPTION_ACCESS_VIOLATION writing address 0x0000",
            "LogWindows: Error: [Callstack] 0x00007ff6 Marvel-Win64-Shipping.exe!UnknownFunction []",
            "LogWindows: Error: Crash in runnable thread FAsyncLoadingThread",
        ], summary.CrashBlock);
    }

    [Fact]
    public void Parse_TakesEachTeamsLastHeroIdListBeforeTheCrash()
    {
        var summary = RivalsLogSummary.Parse(CrashingLog);

        // Not the teammates_/final_/mixed_ variants, not a list without a side, and not the list logged after the crash block.
        Assert.Equal([1032, 1050, 1060], summary.AllyHeroIds);
        Assert.Equal([1042, 1048], summary.EnemyHeroIds);
    }

    [Fact]
    public void Parse_ListsCharacterActorHeroesFromTheLinesBeforeTheCrash()
    {
        var summary = RivalsLogSummary.Parse(CrashingLog);

        Assert.Equal([1048, 1032], summary.CharacterLoadHeroIds);
    }

    [Fact]
    public void Parse_CharacterLoadsOutsideTheWindowAreIgnored()
    {
        var lines = new List<string> { "1048_CharacterBP_C_1 loaded" };
        lines.AddRange(Enumerable.Repeat("filler", RivalsLogSummary.CharacterLoadWindow - 1));
        lines.Add("1032_CharacterBP_C_2 loaded");
        lines.Add("LogWindows: Error: === Critical error: ===");

        var summary = RivalsLogSummary.Parse(lines);

        // 1048 is the 301st line back: one outside the window.
        Assert.Equal([1032], summary.CharacterLoadHeroIds);
    }

    [Fact]
    public void Parse_CollectsSerializationAndFatalLines_WithoutTheTimestampPrefix()
    {
        var summary = RivalsLogSummary.Parse(CrashingLog);

        Assert.Equal(
        [
            "LogUObjectGlobals: Error: ObjectSerializationError: Bad name index 12345/6",
            "LogWindows: Error: Fatal error!",
        ], summary.ErrorLines);
    }

    [Fact]
    public void Parse_ALogWithoutACrash_HasNoCrashFieldsButStillSummarisesTheRest()
    {
        string[] lines =
        [
            $"{P}LogInit: Display: Engine Version: 5.3.2-3939753+++depot_marvel+S10.5_release",
            "LogPakFile: Display: Mounted IoStore container \"../../../Marvel/Content/Paks/~mods/zA_9999999_P.utoc\"",
            $"{P}LogHeroBonds: Display: is_allies=True hero_id_list=[1, 2]",
            $"{P}LogHeroBonds: Display: is_allies=True hero_id_list=[3, 4]",
            "1048_CharacterBP_C_1 loaded",
        ];

        var summary = RivalsLogSummary.Parse(lines);

        Assert.False(summary.Crashed);
        Assert.Equal(3939753, summary.Build);
        Assert.Equal(["zA_9999999_P.utoc"], summary.Mods);
        Assert.Equal([3, 4], summary.AllyHeroIds);
        Assert.Empty(summary.EnemyHeroIds);
        Assert.Empty(summary.CharacterLoadHeroIds);
        Assert.Empty(summary.CrashBlock);
        Assert.Empty(summary.ErrorLines);
    }

    [Fact]
    public void Parse_AnEmptyLog_IsAnEmptySummary()
    {
        var summary = RivalsLogSummary.Parse([]);

        Assert.Null(summary.EngineVersion);
        Assert.Null(summary.Build);
        Assert.False(summary.Crashed);
        Assert.Empty(summary.Mods);
        Assert.Empty(summary.AllyHeroIds);
        Assert.Empty(summary.EnemyHeroIds);
    }

    [Fact]
    public void Parse_ACrashBlockWithoutTheRunnableThreadLine_RunsToTheEndOfTheLog()
    {
        var summary = RivalsLogSummary.Parse(["LogWindows: Error: === Critical error: ===", "more", "and more"]);

        Assert.Equal(["LogWindows: Error: === Critical error: ===", "more", "and more"], summary.CrashBlock);
    }

    [Fact]
    public void DiffMods_ReportsEachSideOnly_IgnoringCase()
    {
        var first = RivalsLogSummary.Parse(MountLines("zBWBikini_9999999_P.utoc", "Shared_P.utoc", "zPsyVengeance_9999999_P.utoc"));
        var second = RivalsLogSummary.Parse(MountLines("shared_p.utoc", "Other_P.utoc"));

        var diff = RivalsLogSummary.DiffMods(first, second);

        Assert.Equal(["zBWBikini_9999999_P.utoc", "zPsyVengeance_9999999_P.utoc"], diff.OnlyInFirst);
        Assert.Equal(["Other_P.utoc"], diff.OnlyInSecond);
    }

    [Fact]
    public void DiffMods_IdenticalSessions_AreEmpty()
    {
        var summary = RivalsLogSummary.Parse(MountLines("a.utoc"));

        var diff = RivalsLogSummary.DiffMods(summary, summary);

        Assert.Empty(diff.OnlyInFirst);
        Assert.Empty(diff.OnlyInSecond);
    }

    [Theory]
    [InlineData("LogInit: Display: Engine Version: 5.3.2-4008108+++depot_marvel+S10.5_release", true, 4008108)]
    [InlineData("LogCsvProfiler: Display: Metadata set : engineversion=\"5.3.2-4008108+++depot_marvel+S10.5_release\"", false, 0)]
    [InlineData("LogInit: Display: Compatible Engine Version: 5.3.2-3998123+++depot_marvel+S10.5_release", false, 0)]
    public void TryFindEngineVersion_OnlyMatchesTheEngineVersionLine(string line, bool found, int build)
    {
        Assert.Equal(found, RivalsLogSummary.TryFindEngineVersion(line, out _, out var parsed));
        Assert.Equal(build, parsed);
    }

    private static string[] MountLines(params string[] mods) =>
        [.. mods.Select(mod => $"LogPakFile: Display: Mounted IoStore container \"../../../Marvel/Content/Paks/~mods/{mod}\"")];
}
