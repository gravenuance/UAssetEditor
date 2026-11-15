using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using UAssetEditor.Core;
using UAssetEditor.Core.Tests;
using UAssetEditor.Mcp.Hosting;
using UAssetEditor.Mcp.Tests.Rig;
using UAssetEditor.Mcp.Tools;

namespace UAssetEditor.Mcp.Tests;

public sealed class HelperTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly TestLinks _links = new();

    public void Dispose()
    {
        _links.Dispose(); // before the folder: a junction must go as a link, not be walked into
        _folder.Dispose();
    }

    // ToolPaths

    [Fact]
    public void Absolute_TakesAnAbsolutePath_InItsFullForm()
    {
        var path = _folder.Combine("a", "..", "b.uasset");

        Assert.Equal(_folder.Combine("b.uasset"), ToolPaths.Absolute(path, "file"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path.uasset")]
    [InlineData("C:drive-relative.uasset")]
    [InlineData("\\\\server\\share\\a.uasset")]
    [InlineData("\\\\?\\C:\\a.uasset")]
    [InlineData("//server/share/a.uasset")]
    [InlineData("\\??\\UNC\\server\\share\\a.uasset")]
    [InlineData("\\??\\C:\\a.uasset")]
    [InlineData("/??/UNC/server/share/a.uasset")]
    [InlineData("C:\\mods\\a.pak:hidden.pak")]
    public void Absolute_RefusesBlankRelativeAndNetworkPaths(string path)
    {
        Assert.Throws<McpException>(() => ToolPaths.Absolute(path, "file"));
    }

    [Fact]
    public void Absolute_RefusesControlCharacters_AndVeryLongPaths()
    {
        Assert.Throws<McpException>(() => ToolPaths.Absolute(_folder.Combine("a\nb.uasset"), "file"));
        Assert.Throws<McpException>(() => ToolPaths.Absolute(_folder.Combine(new string('x', 1100)), "file"));
    }

    [Fact]
    public void ExistingFile_ChecksTheExtensionIgnoringCase_AndThatTheFileExists()
    {
        var file = _folder.Combine("A.UASSET");
        File.WriteAllText(file, "x");

        Assert.Equal(file, ToolPaths.ExistingFile(file, "file", ".uasset", ".umap"));
        Assert.Throws<McpException>(() => ToolPaths.ExistingFile(file, "file", ".pak"));
        Assert.Throws<McpException>(() => ToolPaths.ExistingFile(_folder.Combine("B.uasset"), "file", ".uasset"));
    }

    [Fact]
    public void NewFolder_RefusesANonEmptyFolderAndAFile_UnlessOverwriting()
    {
        var full = _folder.Combine("full");
        Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "x"), "x");
        var empty = _folder.Combine("empty");
        Directory.CreateDirectory(empty);
        var file = _folder.Combine("file");
        File.WriteAllText(file, "x");

        Assert.Throws<McpException>(() => ToolPaths.NewFolder(full, "output", overwrite: false));
        Assert.Equal(full, ToolPaths.NewFolder(full, "output", overwrite: true));
        Assert.Equal(empty, ToolPaths.NewFolder(empty, "output", overwrite: false));
        Assert.Equal(_folder.Combine("new"), ToolPaths.NewFolder(_folder.Combine("new"), "output", overwrite: false));
        Assert.Throws<McpException>(() => ToolPaths.NewFolder(file, "output", overwrite: true));
    }

    [Fact]
    public void RefuseInside_CatchesAFolderItsChildrenAndNotALookalikeSibling()
    {
        var folder = _folder.Combine("mod");

        Assert.Throws<McpException>(() => ToolPaths.RefuseInside(Path.Combine(folder, "out.pak"), folder, "output", "source"));
        ToolPaths.RefuseInside(_folder.Combine("mod2", "out.pak"), folder, "output", "source");
        ToolPaths.RefuseInside(folder, folder, "output", "source");
    }

    [Fact]
    public void RefuseSame_ComparesFullPaths()
    {
        Assert.Throws<McpException>(() => ToolPaths.RefuseSame(_folder.Combine("a.pak"), _folder.Combine("a.pak"), "pak", "output"));
        ToolPaths.RefuseSame(_folder.Combine("a.pak"), _folder.Combine("b.pak"), "pak", "output");
    }

    [Fact]
    public void RefuseSame_SeesThroughAJunction_EvenForAnOutputThatDoesNotExistYet()
    {
        var real = _folder.Combine("real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "a.pak"), "x");
        var link = _folder.Combine("link");
        _links.MakeJunction(link, real);

        Assert.Throws<McpException>(() => ToolPaths.RefuseSame(Path.Combine(real, "a.pak"), Path.Combine(link, "a.pak"), "pak", "output"));
        Assert.Throws<McpException>(() => ToolPaths.RefuseSame(Path.Combine(link, "a.pak"), Path.Combine(real, "A.PAK"), "pak", "output"));
        ToolPaths.RefuseSame(Path.Combine(real, "a.pak"), Path.Combine(link, "b.pak"), "pak", "output");
    }

    [Fact]
    public void RefuseSame_SeesAHardLinkToTheSameFile_ButNotACopy()
    {
        var pak = _folder.Combine("a.pak");
        File.WriteAllText(pak, "x");
        var hardLink = _folder.Combine("b.pak");
        TestLinks.MakeHardLink(hardLink, pak);
        var copy = _folder.Combine("c.pak");
        File.Copy(pak, copy);

        Assert.Throws<McpException>(() => ToolPaths.RefuseSame(pak, hardLink, "pak", "output"));
        ToolPaths.RefuseSame(pak, copy, "pak", "output");
    }

    [Fact]
    public void RefuseSame_SeesThroughAnEightDotThreeName()
    {
        var folder = _folder.Combine("A Folder With A Long Name");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.pak"), "x");

        Assert.Throws<McpException>(() => ToolPaths.RefuseSame(Path.Combine(folder, "a.pak"), Path.Combine(TestLinks.ShortName(folder), "a.pak"), "pak", "output"));
    }

    [Fact]
    public void RefuseInside_SeesAnOutputReachedThroughAJunctionIntoTheFolder()
    {
        var folder = _folder.Combine("mod");
        var elsewhere = _folder.Combine("elsewhere");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(elsewhere);
        _links.MakeJunction(Path.Combine(elsewhere, "into"), folder);

        Assert.Throws<McpException>(() => ToolPaths.RefuseInside(Path.Combine(elsewhere, "into", "out.pak"), folder, "output", "source"));
        ToolPaths.RefuseInside(Path.Combine(elsewhere, "out.pak"), folder, "output", "source");
    }

    [Fact]
    public async Task ALinkCycle_IsRefusedWithAClearError_InThePathChecksAndThePathLocks()
    {
        var first = _folder.Combine("first");
        var second = _folder.Combine("second");
        _links.MakeFolderLink(first, second);
        _links.MakeFolderLink(second, first);
        var looped = Path.Combine(first, "a.uasset");

        var inside = Assert.Throws<PathNotPlaceableException>(() => ToolPaths.RefuseInside(looped, _folder.Combine("mod"), "output", "source"));
        var same = Assert.Throws<PathNotPlaceableException>(() => ToolPaths.RefuseSame(looped, _folder.Combine("b.pak"), "pak", "output"));
        var locked = await Assert.ThrowsAsync<PathNotPlaceableException>(() => PathLocks.RunAsync([looped], () => Task.FromResult(0), TestContext.Current.CancellationToken));

        Assert.All([inside, same, locked], error => Assert.Contains("Too many levels of links", error.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void APathTooLongToResolve_IsAToolError_InThePathChecks()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows is where a path can pass 32,767 characters
        var tooLong = Path.Combine(_folder.Path, new string('a', 40_000));

        Assert.Throws<McpException>(() => ToolPaths.RefuseInside(tooLong, _folder.Combine("mod"), "output", "source"));
        Assert.Throws<McpException>(() => ToolPaths.RefuseSame(tooLong, _folder.Combine("b.pak"), "pak", "output"));
    }

    // Untrusted / ToolInput

    [Fact]
    public void Clean_TurnsControlCharactersIntoSpaces_AndKeepsOrdinarySpacing()
    {
        Assert.Equal("a b  c d", Untrusted.Clean("a\nb  c\u001bd"));
        Assert.Equal("", Untrusted.Clean(null));
        Assert.Equal("", Untrusted.Clean(""));
    }

    [Fact]
    public void Clean_TurnsInvisibleFormatCharactersIntoSpaces_SoNoHiddenTextReachesTheModel()
    {
        // A right-to-left override, a zero-width space, a Unicode tag letter (a surrogate pair) and a lone surrogate.
        Assert.Equal("a b c d e", Untrusted.Clean("a\u202Eb\u200Bc\U000E0041d\uD800e"));
    }

    [Fact]
    public void Clean_NeverCutsASurrogatePairInHalf()
    {
        Assert.Equal("ab...", Untrusted.Clean("ab\U0001F600", 3));
        Assert.Equal("ab\U0001F600", Untrusted.Clean("ab\U0001F600", 4));
    }

    [Fact]
    public void Clean_CutsAtTheLimitWithAnEllipsis_AndLeavesTextAtTheLimitAlone()
    {
        Assert.Equal("abcde", Untrusted.Clean("abcde", 5));
        Assert.Equal("abcde...", Untrusted.Clean("abcdef", 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Untrusted.Clean("x", 0));
    }

    [Fact]
    public void CleanLines_CleansEachLine_AndStopsWhenTheTotalIsReached()
    {
        Assert.Equal("one\ntwo", Untrusted.CleanLines("one\r\n\r\ntwo\n", 100));
        Assert.Equal("aaaa\nbbbb...", Untrusted.CleanLines("aaaa\nbbbb\ncccc\ndddd", 12));
        Assert.Equal("xx...", Untrusted.CleanLines("xxxxxxxx", 100, maxLineLength: 2));
    }

    [Fact]
    public void ToolInput_ChecksRangesNamesAndRequiredText()
    {
        Assert.Equal(5, ToolInput.InRange(5, 1, 5, "n"));
        Assert.Throws<McpException>(() => ToolInput.InRange(6, 1, 5, "n"));
        Assert.Equal("abc", ToolInput.Required("  abc ", "x"));
        Assert.Throws<McpException>(() => ToolInput.Required(" ", "x"));
        Assert.Throws<McpException>(() => ToolInput.Required("abcdef", "x", maxLength: 5));
        Assert.Null(ToolInput.Optional("  ", "x"));
        Assert.Equal("original", ToolInput.OneOf("ORIGINAL", "layer", "modded", "original"));
        Assert.Throws<McpException>(() => ToolInput.OneOf("both", "layer", "modded", "original"));
        Assert.Equal(UAssetAPI.PakVersion.V9, ToolInput.Name<UAssetAPI.PakVersion>("v9", "pakVersion"));
        Assert.Throws<McpException>(() => ToolInput.Name<UAssetAPI.PakVersion>("9", "pakVersion"));
        Assert.Throws<McpException>(() => ToolInput.Name<UAssetAPI.PakVersion>("", "pakVersion"));
    }

    [Fact]
    public void ToolInput_Name_RefusesSeveralCommaSeparatedNames()
    {
        // Enum.TryParse ORs these into Initial|NoTimestamps = 1|2 = 3, which is the defined member V3 that nobody named.
        Assert.Throws<McpException>(() => ToolInput.Name<UAssetAPI.PakVersion>("V1,V2", "pakVersion"));
    }

    // LogLines / ProgressSink

    [Fact]
    public void LogLines_KeepsTheFirstLinesCleaned_AndCountsTheRest()
    {
        var lines = new LogLines(2);

        lines.Add("one\nline");
        lines.Add("two");
        lines.Add("three");
        lines.Add("four");

        Assert.Equal(["one line", "two", "... 2 more line(s) not shown"], lines.ToList());
    }

    [Fact]
    public void ProgressSink_SendsAboutAHundredUpdatesForAHugeRun_AndAlwaysTheLastOne()
    {
        var collected = new CollectedProgress();
        var sink = new ProgressSink(collected, "Items");

        for (var done = 1; done <= 10_000; done++) sink.Report((done, 10_000));

        Assert.InRange(collected.Values.Count, 100, 101);
        var last = collected.Values[^1];
        Assert.Equal(10_000, last.Progress);
        Assert.Equal(10_000, last.Total);
        Assert.Equal("Items: 10000 of 10000", last.Message);
    }

    [Fact]
    public void ProgressSink_SendsEveryStepOfASmallRun_AndIsQuietWithoutATokenOrItems()
    {
        var collected = new CollectedProgress();
        var sink = new ProgressSink(collected, "Items");

        for (var done = 1; done <= 3; done++) sink.Report((done, 3));
        sink.Report((0, 0));
        new ProgressSink(null, "Items").Report((1, 1));

        Assert.Equal([1f, 2f, 3f], collected.Values.Select(v => v.Progress));
    }

    [Fact]
    public void ProgressSink_DropsAStepThatArrivesAfterALaterOne_SoProgressNeverGoesBack()
    {
        var collected = new CollectedProgress();
        var sink = new ProgressSink(collected, "Items");

        // Parallel workers can report in any order.
        sink.Report((2, 3));
        sink.Report((1, 3));
        sink.Report((3, 3));

        Assert.Equal([2f, 3f], collected.Values.Select(v => v.Progress));
    }

    // Logging

    [Fact]
    public void ParseLevel_ReadsALevelName_AndFallsBackToInformation()
    {
        Assert.Equal(LogLevel.Warning, McpLogging.ParseLevel("warning"));
        Assert.Equal(LogLevel.Error, McpLogging.ParseLevel("Error"));
        Assert.Equal(LogLevel.Information, McpLogging.ParseLevel(null));
        Assert.Equal(LogLevel.Information, McpLogging.ParseLevel("loud"));
        Assert.Equal(LogLevel.Information, McpLogging.ParseLevel("42"));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception) + (exception is null ? "" : Environment.NewLine + exception)));
    }

    [Fact]
    public void LogFailure_WritesTheWholeExceptionChain_WithAnAesKeyRedacted()
    {
        const string key = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF"; // gitleaks:allow (a made-up test key)
        var log = new RecordingLogger();
        var failure = new InvalidOperationException($"retoc --aes-key 0x{key} failed", new IOException($"inner {key}"));

        McpLogging.LogFailure(log, LogLevel.Critical, "Stopped unexpectedly", failure);

        var (level, text) = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Critical, level);
        Assert.Contains("Stopped unexpectedly", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("IOException: inner [redacted]", text, StringComparison.Ordinal);
        Assert.DoesNotContain(key, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LogFailure_WithNoException_LogsTheMessageAlone()
    {
        var log = new RecordingLogger();

        McpLogging.LogFailure(log, LogLevel.Error, "Something odd", exception: null);

        Assert.Contains("Something odd", Assert.Single(log.Lines).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void LogDirectory_IsUnderTheCacheFoldersLogs_NotInTheAppsOwnLogFolder()
    {
        var paths = new AppPaths(name => name == AppPaths.CacheDirectoryVariable ? _folder.Path : null, "unused");

        Assert.Equal(Path.Combine(_folder.Path, "Logs", "Mcp"), McpLogging.LogDirectory(paths));
        Assert.NotEqual(paths.LogsDirectory, McpLogging.LogDirectory(paths));
    }

    [Fact]
    public void PruneOldLogs_DeletesOnlyDailyLogsPastTheRetention()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var old = _folder.Combine("log-20261001.txt");
        var recent = _folder.Combine("log-20261110.txt");
        var other = _folder.Combine("notes.txt");
        foreach (var file in new[] { old, recent, other }) File.WriteAllText(file, "x");
        File.SetLastWriteTimeUtc(old, now.UtcDateTime - TimeSpan.FromDays(40));
        File.SetLastWriteTimeUtc(recent, now.UtcDateTime - TimeSpan.FromDays(4));
        File.SetLastWriteTimeUtc(other, now.UtcDateTime - TimeSpan.FromDays(400));

        var deleted = McpLogging.PruneOldLogs(_folder.Path, now);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));
        Assert.Equal(0, McpLogging.PruneOldLogs(_folder.Combine("missing"), now));
    }
}
