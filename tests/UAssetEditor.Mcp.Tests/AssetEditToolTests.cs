using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetEditor.Core.Tests;
using UAssetEditor.Mcp.Tests.Rig;
using UAssetEditor.Mcp.Tools;
using UAssetEditor.Tooling;

namespace UAssetEditor.Mcp.Tests;

public sealed class AssetEditToolTests : IDisposable
{
    private const string SetCount = "set --export 0 --path Count --value 7";

    private readonly McpRig _rig = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _rig.Dispose();

    private string Sample(Action<UAsset>? customise = null) => AssetFixtures.WriteSample(_rig.Folder, "Sample.uasset", customise);

    private static bool HasBackup(string uasset) =>
        File.Exists(uasset + ".bak") || File.Exists(Path.ChangeExtension(uasset, ".uexp") + ".bak");

    [Fact]
    public async Task PreviewEdit_ReportsOldAndNewValue_AndWritesNothing()
    {
        var path = Sample();
        var before = AssetFixtures.Snapshot(path);

        var result = await _rig.Edits.PreviewEditAsync(path, SetCount, cancellationToken: _ct);

        var op = Assert.Single(result.Ops);
        Assert.Equal("Count: 5 -> 7", op.Text);
        Assert.Equal(1, op.Line);
        Assert.Equal("set", op.Verb);
        Assert.False(result.Saved);
        Assert.Equal(1, result.OpsRun);
        Assert.Equal(before, AssetFixtures.Snapshot(path));
        Assert.False(HasBackup(path));
        Assert.Equal("5", AssetFixtures.ReadProperty(path, "Count"));
    }

    [Fact]
    public async Task EditAsset_WithoutSave_IsADryRun()
    {
        var path = Sample();
        var before = AssetFixtures.Snapshot(path);

        var result = await _rig.Edits.EditAssetAsync(path, SetCount, cancellationToken: _ct);

        Assert.False(result.Saved);
        Assert.Contains("dry run", result.Note, StringComparison.Ordinal);
        Assert.Equal(before, AssetFixtures.Snapshot(path));
        Assert.False(HasBackup(path));
    }

    [Fact]
    public async Task EditAsset_WithSave_WritesTheEdit_AndKeepsBothOriginalsAsBak()
    {
        var path = Sample();
        var before = AssetFixtures.Snapshot(path);

        var result = await _rig.Edits.EditAssetAsync(path, SetCount, save: true, cancellationToken: _ct);

        Assert.True(result.Saved);
        Assert.Equal(path + ".bak", result.Backup);
        Assert.Equal("7", AssetFixtures.ReadProperty(path, "Count"));
        Assert.Equal(before[0], await File.ReadAllBytesAsync(path + ".bak", _ct));
        Assert.Equal(before[1], await File.ReadAllBytesAsync(Path.ChangeExtension(path, ".uexp") + ".bak", _ct));
    }

    [Fact]
    public async Task EditAsset_WithSaveAndNoBackup_LeavesNoBak()
    {
        var path = Sample();

        var result = await _rig.Edits.EditAssetAsync(path, SetCount, save: true, backup: false, cancellationToken: _ct);

        Assert.True(result.Saved);
        Assert.Null(result.Backup);
        Assert.False(HasBackup(path));
        Assert.Equal("7", AssetFixtures.ReadProperty(path, "Count"));
    }

    [Fact]
    public async Task EditAsset_RunsSeveralOpsInOneCall_IncludingQuotedValuesAndComments()
    {
        var path = Sample();
        var ops = string.Join('\n', "# bump the count", SetCount, "", "set --export 0 --path DisplayName --value \"Hello Edited\"");

        var result = await _rig.Edits.EditAssetAsync(path, ops, save: true, cancellationToken: _ct);

        Assert.Equal([2, 4], result.Ops.Select(o => o.Line));
        Assert.Equal("7", AssetFixtures.ReadProperty(path, "Count"));
        Assert.Equal("Hello Edited", AssetFixtures.ReadProperty(path, "DisplayName"));
    }

    [Fact]
    public async Task EditAsset_WithAnOpThatFails_ReportsItSkipped_AndSavesNothing()
    {
        var path = Sample();
        var before = AssetFixtures.Snapshot(path);
        var ops = SetCount + "\nset --export 0 --path No.Such.Path --value 1";

        var result = await _rig.Edits.EditAssetAsync(path, ops, save: true, cancellationToken: _ct);

        Assert.False(result.Saved);
        Assert.Equal(1, result.OpsRun);
        Assert.Equal(1, result.OpsSkipped);
        Assert.True(result.Ops[1].Skipped);
        Assert.Contains("No property at path", result.Ops[1].Text, StringComparison.Ordinal);
        Assert.Contains("Nothing was saved", result.Note, StringComparison.Ordinal);
        Assert.Equal(before, AssetFixtures.Snapshot(path));
        Assert.False(HasBackup(path));
    }

    [Theory]
    [InlineData("frobnicate --export 0", "unknown op 'frobnicate'")]
    [InlineData("set --export 0 --path Count --valeu 7", "Unknown option --valeu")]
    public async Task EditAsset_WithABadOpLine_StopsBeforeOpeningTheAsset_NamingTheLine(string ops, string expected)
    {
        var path = Sample();
        var before = AssetFixtures.Snapshot(path);

        var error = await Assert.ThrowsAsync<ArgException>(() => _rig.Edits.EditAssetAsync(path, SetCount + "\n" + ops, save: true, cancellationToken: _ct));

        Assert.Contains("ops:2:", error.Message, StringComparison.Ordinal);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Equal(before, AssetFixtures.Snapshot(path));
    }

    [Fact]
    public async Task EditAsset_WithAMissingFile_SaysSo()
    {
        var error = await Assert.ThrowsAsync<McpException>(() => _rig.Edits.EditAssetAsync(_rig.Folder.Combine("Nope.uasset"), SetCount, save: true, cancellationToken: _ct));

        Assert.Contains("not found", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("# only a comment\n\n# another")]
    public async Task PreviewEdit_WithNoOps_AsksForSome(string ops)
    {
        await Assert.ThrowsAsync<McpException>(() => _rig.Edits.PreviewEditAsync(Sample(), ops, cancellationToken: _ct));
    }

    [Fact]
    public async Task PreviewEdit_RefusesMoreLinesThanItWillRun()
    {
        var ops = string.Join('\n', Enumerable.Repeat(SetCount, 2001));

        var error = await Assert.ThrowsAsync<McpException>(() => _rig.Edits.PreviewEditAsync(Sample(), ops, cancellationToken: _ct));

        Assert.Contains("2000 lines", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewEdit_CapsTheTextOfADump_AndKeepsItOnCleanLines()
    {
        var path = Sample(asset => TestAssets.AddStructArray(asset, (NormalExport)asset.Exports[0], "Items", [.. Enumerable.Range(1, 600)]));

        var result = await _rig.Edits.PreviewEditAsync(path, "dump --export 0 --filter Items", cancellationToken: _ct);

        var text = Assert.Single(result.Ops).Text;
        Assert.InRange(text.Length, 3900, 4003);
        Assert.EndsWith("...", text, StringComparison.Ordinal);
        Assert.StartsWith("Items =\nItems[0] =\nItems[0].Value = 1\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewEdit_OmitsOpTextOnceTheResultIsAsBigAsItMayGet()
    {
        var path = Sample(asset => TestAssets.AddStructArray(asset, (NormalExport)asset.Exports[0], "Items", [.. Enumerable.Range(1, 600)]));
        var ops = string.Join('\n', Enumerable.Repeat("dump --export 0 --filter Items", 40));

        var result = await _rig.Edits.PreviewEditAsync(path, ops, cancellationToken: _ct);

        Assert.True(result.Truncated);
        Assert.Equal(40, result.Ops.Count);
        Assert.Equal("(omitted: result size limit)", result.Ops[^1].Text);
    }

    [Fact]
    public async Task EditAsset_WhenCancelled_WritesNothing()
    {
        var path = Sample();
        var before = AssetFixtures.Snapshot(path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _rig.Edits.EditAssetAsync(path, SetCount, save: true, cancellationToken: McpRig.Cancelled));

        Assert.Equal(before, AssetFixtures.Snapshot(path));
        Assert.False(HasBackup(path));
    }

    [Fact]
    public void TheEditToolDescriptions_NameEveryVerbTheScriptRunnerAccepts()
    {
        foreach (var method in new[] { nameof(AssetEditTools.EditAssetAsync), nameof(AssetEditTools.PreviewEditAsync) })
        {
            var description = typeof(AssetEditTools).GetMethod(method)!.GetCustomAttribute<DescriptionAttribute>()!.Description;

            foreach (var verb in ScriptOps.Verbs)
                Assert.Contains(verb, description, StringComparison.Ordinal);
        }
    }
}
