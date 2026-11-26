using UAssetEditor.Cli;
using UAssetEditor.Mcp.Tests.Rig;
using UAssetEditor.Tooling;

namespace UAssetEditor.Core.Tests;

/// <summary>uacli script with --save writes the asset only when every op ran, as the MCP edit_asset tool does.</summary>
public sealed class CliScriptTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string OpsFile(string text)
    {
        var path = _folder.Combine("ops.txt");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Script_WithSave_AndAnOpThatFails_SavesNothing_AndExitsNonZero()
    {
        var asset = AssetFixtures.WriteSample(_folder);
        var before = AssetFixtures.Snapshot(asset);
        var ops = OpsFile("set --export 0 --path Count --value 7\nset --export 0 --path No.Such.Path --value 1\n");

        var exit = ScriptRunner.Run(new ArgReader([asset, "--ops", ops, "--save", "--version", "VER_UE4_27"]));

        Assert.Equal(1, exit);
        Assert.Equal(before, AssetFixtures.Snapshot(asset));
    }

    [Fact]
    public void Script_WithSave_AndEveryOpRun_SavesTheAsset()
    {
        var asset = AssetFixtures.WriteSample(_folder);
        var ops = OpsFile("set --export 0 --path Count --value 7\n");

        var exit = ScriptRunner.Run(new ArgReader([asset, "--ops", ops, "--save", "--version", "VER_UE4_27"]));

        Assert.Equal(0, exit);
        Assert.Equal("7", AssetFixtures.ReadProperty(asset, "Count"));
    }

    [Fact]
    public void ScriptPlan_WithSave_CountsAnAssetWithASkippedOpAsFailed_AndSavesOnlyTheOther()
    {
        var good = AssetFixtures.WriteSample(_folder, "Good.uasset");
        var bad = AssetFixtures.WriteSample(_folder, "Bad.uasset");
        var before = AssetFixtures.Snapshot(bad);
        var goodOps = OpsFile("set --export 0 --path Count --value 7\n");
        var badOps = _folder.Combine("bad-ops.txt");
        File.WriteAllText(badOps, "set --export 0 --path Count --value 8\nset --export 0 --path No.Such.Path --value 1\n");
        var plan = _folder.Combine("plan.txt");
        File.WriteAllText(plan, $"{good}\t{goodOps}\n{bad}\t{badOps}\n");

        var exit = ScriptRunner.Run(new ArgReader(["--plan", plan, "--save", "--version", "VER_UE4_27"]));

        Assert.Equal(1, exit);
        Assert.Equal("7", AssetFixtures.ReadProperty(good, "Count"));
        Assert.Equal(before, AssetFixtures.Snapshot(bad));
    }
}
