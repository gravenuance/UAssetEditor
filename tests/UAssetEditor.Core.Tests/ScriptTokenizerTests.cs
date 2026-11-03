using UAssetEditor.Cli;

namespace UAssetEditor.Core.Tests;

public class ScriptTokenizerTests
{
    [Fact]
    public void Tokenize_SplitsOnWhitespace_AndQuotesGroupSpaces()
    {
        var tokens = ScriptRunner.Tokenize("set --path Row.Name --value \"two words\"");

        Assert.Equal(["set", "--path", "Row.Name", "--value", "two words"], tokens);
    }

    [Fact]
    public void Parse_AcceptsTheTagOps()
    {
        var ops = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(ops, ["list-tags --export Default__", "set-tag --export Default__ --tag Link_ShowAnim --value 3"]);

            Assert.Equal(2, ScriptRunner.Parse(ops).Count);
        }
        finally
        {
            File.Delete(ops);
        }
    }

    [Fact]
    public void Tokenize_EscapedQuote_IsKeptLiterally()
    {
        var tokens = ScriptRunner.Tokenize("set --value [{\\\"Alias\\\":\\\"BurstGauge_DeadNormal3\\\"}]");

        Assert.Equal(["set", "--value", "[{\"Alias\":\"BurstGauge_DeadNormal3\"}]"], tokens);
    }

    [Fact]
    public void Tokenize_EscapedQuoteInsideQuotes_KeepsTheSpaces()
    {
        var tokens = ScriptRunner.Tokenize("set --value \"[{\\\"Alias\\\":\\\"A\\\"}, {\\\"Alias\\\":\\\"B\\\"}]\"");

        Assert.Equal(["set", "--value", "[{\"Alias\":\"A\"}, {\"Alias\":\"B\"}]"], tokens);
    }

    [Fact]
    public void Tokenize_QuotedPathEndingInBackslash_AtLineEnd_KeepsTheBackslash()
    {
        var tokens = ScriptRunner.Tokenize("dump --out \"D:\\my mods\\\"");

        Assert.Equal(["dump", "--out", "D:\\my mods\\"], tokens);
    }

    [Fact]
    public void Tokenize_QuotedPathEndingInBackslash_BeforeMoreOptions_ClosesTheQuote()
    {
        var tokens = ScriptRunner.Tokenize("dump --out \"D:\\my mods\\\" --export 3");

        Assert.Equal(["dump", "--out", "D:\\my mods\\", "--export", "3"], tokens);
    }

    [Fact]
    public void Tokenize_BackslashNotBeforeAQuote_StaysAsIs()
    {
        var tokens = ScriptRunner.Tokenize("dump --out D:\\mods\\x.txt");

        Assert.Equal(["dump", "--out", "D:\\mods\\x.txt"], tokens);
    }
}
