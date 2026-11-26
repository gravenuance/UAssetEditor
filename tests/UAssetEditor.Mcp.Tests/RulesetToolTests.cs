using ModelContextProtocol;
using UAssetAPI;
using UAssetEditor.Mcp.Tests.Rig;
using UAssetEditor.Mcp.Tools;
using UAssetEditor.Tooling;

namespace UAssetEditor.Mcp.Tests;

public sealed class RulesetToolTests : IDisposable
{
    // Sets every property whose path contains "Count" (the sample has exactly one: Count).
    private const string SetCountTo9 =
        """{"name":"t","scope":{"propertyNameTerms":[{"text":"Count"}]},"rules":[{"kind":"setValue","newValue":"9"}]}""";

    private readonly McpRig _rig = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _rig.Dispose();

    private string Sample(string name = "Sample.uasset", Action<UAsset>? customise = null) =>
        AssetFixtures.WriteSample(_rig.Folder, name, customise);

    private static bool HasBackup(string uasset) =>
        File.Exists(uasset + ".bak") || File.Exists(Path.ChangeExtension(uasset, ".uexp") + ".bak");

    [Fact]
    public async Task PreviewRuleset_ListsTheChange_AndWritesNothing()
    {
        var path = Sample();
        var before = AssetFixtures.Snapshot(path);

        var result = await _rig.Rulesets.PreviewRulesetAsync(path, SetCountTo9, cancellationToken: _ct);

        Assert.False(result.Applied);
        Assert.Equal(1, result.AssetsChanged);
        Assert.Equal(1, result.Changes);
        var asset = Assert.Single(result.Assets);
        Assert.Equal("WouldChange", asset.Status);
        var change = Assert.Single(asset.Changes);
        Assert.Equal("Count", change.Path);
        Assert.Equal("5", change.Old);
        Assert.Equal("9", change.New);
        Assert.Contains("dry run", result.Note, StringComparison.Ordinal);
        Assert.Equal(before, AssetFixtures.Snapshot(path));
        Assert.False(HasBackup(path));
    }

    [Fact]
    public async Task PreviewRuleset_OverAFolder_CoversEveryAsset_AndReportsProgress()
    {
        Sample("A.uasset");
        Sample("Sub/B.uasset");
        var progress = new CollectedProgress();

        var result = await _rig.Rulesets.PreviewRulesetAsync(_rig.Folder.Path, SetCountTo9, progress: progress, cancellationToken: _ct);

        Assert.Equal(2, result.AssetsChanged);
        Assert.Equal(2, result.Changes);
        Assert.Contains(progress.Values, p => p.Progress == 2 && p.Total == 2);
    }

    [Fact]
    public async Task PreviewRuleset_CapsTheListedChanges_ButCountsThemAll()
    {
        foreach (var name in (string[])["A.uasset", "B.uasset", "C.uasset"]) Sample(name);

        var result = await _rig.Rulesets.PreviewRulesetAsync(_rig.Folder.Path, SetCountTo9, maxChanges: 2, cancellationToken: _ct);

        Assert.Equal(3, result.Changes);
        Assert.Equal(2, result.Assets.Sum(a => a.Changes.Count));
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task PreviewRuleset_RefusesAMaxChangesOutsideTheRange()
    {
        var path = Sample();

        await Assert.ThrowsAsync<McpException>(() => _rig.Rulesets.PreviewRulesetAsync(path, SetCountTo9, maxChanges: 0, cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.Rulesets.PreviewRulesetAsync(path, SetCountTo9, maxChanges: 1001, cancellationToken: _ct));
    }

    [Fact]
    public async Task ApplyRuleset_WritesTheChange_AndKeepsBothOriginalsAsBak()
    {
        var path = Sample();
        var before = AssetFixtures.Snapshot(path);

        var result = await _rig.Rulesets.ApplyRulesetAsync(path, SetCountTo9, cancellationToken: _ct);

        Assert.True(result.Applied);
        Assert.Equal(1, result.AssetsChanged);
        Assert.Equal("Saved", Assert.Single(result.Assets).Status);
        Assert.Equal("9", AssetFixtures.ReadProperty(path, "Count"));
        Assert.Equal(before[0], await File.ReadAllBytesAsync(path + ".bak", _ct));
        Assert.Equal(before[1], await File.ReadAllBytesAsync(Path.ChangeExtension(path, ".uexp") + ".bak", _ct));
    }

    [Fact]
    public async Task ApplyRuleset_WithoutBackup_LeavesNoBak()
    {
        var path = Sample();

        await _rig.Rulesets.ApplyRulesetAsync(path, SetCountTo9, backup: false, cancellationToken: _ct);

        Assert.Equal("9", AssetFixtures.ReadProperty(path, "Count"));
        Assert.False(HasBackup(path));
    }

    [Fact]
    public async Task ApplyRuleset_OverAFolder_SavesWhatItCanAndSkipsWhatItCannotOpen()
    {
        var good = Sample("Good.uasset");
        await File.WriteAllBytesAsync(_rig.Folder.Combine("Broken.uasset"), [1, 2, 3, 4], _ct);

        var result = await _rig.Rulesets.ApplyRulesetAsync(_rig.Folder.Path, SetCountTo9, cancellationToken: _ct);

        Assert.Equal(1, result.AssetsChanged);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Failed);
        Assert.Contains(result.Assets, a => a.Status == "Skipped" && a.Asset.EndsWith("Broken.uasset", StringComparison.Ordinal));
        Assert.Equal("9", AssetFixtures.ReadProperty(good, "Count"));
    }

    [Fact]
    public async Task Ruleset_CanComeFromAFile()
    {
        var path = Sample();
        var rules = _rig.Folder.Combine("rules.json");
        await File.WriteAllTextAsync(rules, SetCountTo9, _ct);

        var result = await _rig.Rulesets.PreviewRulesetAsync(path, rules, cancellationToken: _ct);

        Assert.Equal(1, result.Changes);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"scope":{},"rules":[{"kind":"launchMissiles"}]}""")]
    [InlineData("""{"name":"x","scope":{"valueTerms":[{"text":"a"}]},"rules":[]}""")]
    public async Task Ruleset_ThatIsNotAValidRuleSet_IsRefused_BeforeAnythingOpens(string ruleset)
    {
        var path = Sample();

        await Assert.ThrowsAsync<ArgException>(() => _rig.Rulesets.ApplyRulesetAsync(path, ruleset, cancellationToken: _ct));

        Assert.Equal("5", AssetFixtures.ReadProperty(path, "Count"));
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    [InlineData("25.00:00:00")]
    public async Task Ruleset_WithARegexTimeoutRegexCannotTake_IsRefusedByName(string timeout)
    {
        var ruleset = $$"""{"name":"t","scope":{"propertyNameTerms":[{"text":"Count"}],"regexTimeout":"{{timeout}}"},"rules":[{"kind":"setValue","newValue":"9"}]}""";

        var error = await Assert.ThrowsAsync<ArgException>(() => _rig.Rulesets.PreviewRulesetAsync(Sample(), ruleset, cancellationToken: _ct));

        Assert.Contains("regexTimeout must be more than zero and at most 24 days", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ruleset_NamingAFileThatIsNotThere_SaysSo()
    {
        var error = await Assert.ThrowsAsync<McpException>(
            () => _rig.Rulesets.PreviewRulesetAsync(Sample(), _rig.Folder.Combine("missing.json"), cancellationToken: _ct));

        Assert.Contains("not found", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rulesets_OnAMissingPath_SayWhatIsMissing()
    {
        var missing = _rig.Folder.Combine("Nowhere");

        await Assert.ThrowsAsync<McpException>(() => _rig.Rulesets.PreviewRulesetAsync(missing, SetCountTo9, cancellationToken: _ct));
        await Assert.ThrowsAsync<McpException>(() => _rig.Rulesets.ApplyRulesetAsync(missing, SetCountTo9, cancellationToken: _ct));
    }

    [Fact]
    public async Task ApplyRuleset_AndEditAsset_CalledTogetherOnOneFile_RunOneAfterAnother()
    {
        var path = Sample();
        var vanilla = AssetFixtures.Snapshot(path);

        var calls = Enumerable.Range(0, 4).SelectMany(_ => new Func<Task>[]
        {
            async () => Assert.Equal(0, (await _rig.Rulesets.ApplyRulesetAsync(path, SetCountTo9, cancellationToken: _ct)).Failed),
            async () => Assert.True((await _rig.Edits.EditAssetAsync(path, "set --export 0 --path Count --value 7", save: true, cancellationToken: _ct)).Saved),
        });
        await Task.WhenAll(calls.Select(call => Task.Run(call, _ct)));

        Assert.Contains(AssetFixtures.ReadProperty(path, "Count"), new[] { "7", "9" });
        Assert.Equal(vanilla[0], await File.ReadAllBytesAsync(path + ".orig.bak", _ct));
        Assert.Empty(Directory.GetFiles(_rig.Folder.Path, "*.tmp"));
    }

    [Fact]
    public async Task ApplyRulesetOverAFolder_SavesTheAssetsNobodyHolds_WhileAnotherCallHoldsOne()
    {
        var free = Sample("Sub/A.uasset");
        var held = Sample("Sub/B.uasset");
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() => PathLocks.RunAsync([held], async () =>
        {
            holding.SetResult();
            await release.Task;
            return 0;
        }, _ct), _ct);
        await holding.Task;

        var run = _rig.Rulesets.ApplyRulesetAsync(_rig.Folder.Path, SetCountTo9, cancellationToken: _ct);
        bool savedWhileHeld;
        try
        {
            // The first save of an asset writes its .orig.bak; a run that waits for every lock up front writes none.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(free + ".orig.bak") && DateTime.UtcNow < deadline) await Task.Delay(50, _ct);
            savedWhileHeld = File.Exists(free + ".orig.bak");
            Assert.False(run.IsCompleted);
        }
        finally
        {
            release.SetResult();
        }

        await holder;
        var result = await run;
        Assert.True(savedWhileHeld);
        Assert.Equal(2, result.AssetsChanged);
        Assert.Equal("9", AssetFixtures.ReadProperty(held, "Count"));
    }

    [Fact]
    public async Task ApplyRulesetOverAFolder_AndEditAssetOnAFileInIt_RunOneAfterAnother()
    {
        var inside = Sample("Sub/A.uasset");
        Sample("Sub/B.uasset");
        var vanilla = AssetFixtures.Snapshot(inside);

        var calls = Enumerable.Range(0, 4).SelectMany(_ => new Func<Task>[]
        {
            async () => Assert.Equal(0, (await _rig.Rulesets.ApplyRulesetAsync(_rig.Folder.Path, SetCountTo9, cancellationToken: _ct)).Failed),
            async () => Assert.True((await _rig.Edits.EditAssetAsync(inside, "set --export 0 --path Count --value 7", save: true, cancellationToken: _ct)).Saved),
        });
        await Task.WhenAll(calls.Select(call => Task.Run(call, _ct)));

        Assert.Equal(vanilla[0], await File.ReadAllBytesAsync(inside + ".orig.bak", _ct));
        Assert.Empty(Directory.GetFiles(_rig.Folder.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Rulesets_WhenCancelled_Throw_AndApplyWritesNothing()
    {
        var path = Sample();
        var before = AssetFixtures.Snapshot(path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.Rulesets.PreviewRulesetAsync(path, SetCountTo9, cancellationToken: McpRig.Cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.Rulesets.ApplyRulesetAsync(path, SetCountTo9, cancellationToken: McpRig.Cancelled));

        Assert.Equal(before, AssetFixtures.Snapshot(path));
        Assert.False(HasBackup(path));
    }
}
