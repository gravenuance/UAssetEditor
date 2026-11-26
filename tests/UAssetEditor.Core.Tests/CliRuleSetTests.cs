using UAssetEditor.Cli;
using UAssetEditor.Tooling;

namespace UAssetEditor.Core.Tests;

/// <summary>The rule-set file of uacli batch and repack: the same limits and messages as the MCP ruleset tools.</summary>
public sealed class CliRuleSetTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_RuleSet_" + Guid.NewGuid().ToString("N"));

    public CliRuleSetTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string RuleSetFile(string json)
    {
        var path = Path.Combine(_folder, "rules.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Batch_WithARuleSetThatHasNoRules_RefusesIt()
    {
        var path = RuleSetFile("""{"name":"empty","scope":{},"rules":[]}""");

        var ex = Assert.Throws<ArgException>(() => Commands.Batch(new ArgReader([_folder, "--ruleset", path])));

        Assert.Equal("--ruleset has no rules.", ex.Message);
    }

    [Fact]
    public void Batch_WithARuleSetThatIsNotValidJson_SaysSo()
    {
        var path = RuleSetFile("""{"rules":[{"kind":"noSuchKind"}]}""");

        var ex = Assert.Throws<ArgException>(() => Commands.Batch(new ArgReader([_folder, "--ruleset", path])));

        Assert.StartsWith("--ruleset is not a valid rule set: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Batch_WithAMissingRuleSetFile_SaysWhichFile()
    {
        var path = Path.Combine(_folder, "missing.json");

        var ex = Assert.Throws<ArgException>(() => Commands.Batch(new ArgReader([_folder, "--ruleset", path])));

        Assert.Equal($"--ruleset not found: {path}", ex.Message);
    }

    [Fact]
    public void RuleSetFile_OverTheCallersLimit_IsRefusedUnread()
    {
        var path = RuleSetFile("{" + new string(' ', 100) + "}");

        var ex = Assert.Throws<ArgException>(() => RuleSets.Load(path, "ruleset", maxBytes: 100));

        Assert.Equal("ruleset file is limited to 100 bytes.", ex.Message);
    }

    [Fact]
    public void Batch_TakesARuleSetFileOfAnySize()
    {
        var filler = string.Concat(Enumerable.Repeat("""{"text":"x"},""", 30_000));
        var path = RuleSetFile($$"""{"scope":{"exportNameTerms":[{{filler}}{"text":"y"}]},"rules":[{"kind":"setValue","newValue":"1"}]}""");

        Assert.Equal(0, Commands.Batch(new ArgReader([_folder, "--ruleset", path])));
    }
}
