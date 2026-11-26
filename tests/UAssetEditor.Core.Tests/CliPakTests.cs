using System.Text;
using UAssetEditor.Cli;
using UAssetEditor.Mcp.Tests.Rig;
using UAssetEditor.Tooling;

namespace UAssetEditor.Core.Tests;

/// <summary>uacli's pak commands refuse to replace files the way the MCP archive tools do.</summary>
[Collection("Pak")]
public sealed class CliPakTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Unpack_OverAnExistingFile_RefusesBeforeWritingAnything_UnlessOverwriteIsPassed()
    {
        using var pak = TestPaks.CreateTemp(new Dictionary<string, byte[]>
        {
            ["Content/A.txt"] = Encoding.UTF8.GetBytes("new-a"),
            ["Content/B.txt"] = Encoding.UTF8.GetBytes("new-b"),
        });
        var destination = _folder.Combine("out");
        Directory.CreateDirectory(Path.Combine(destination, "Content"));
        var existing = Path.Combine(destination, "Content", "B.txt");
        File.WriteAllText(existing, "mine");

        var ex = Assert.Throws<ArgException>(() => PakCommands.Unpack(new ArgReader([pak.Path, destination])));

        Assert.Equal("Content/B.txt already exists in the destination. Pass --overwrite to replace existing files.", ex.Message);
        Assert.Equal("mine", File.ReadAllText(existing));
        Assert.False(File.Exists(Path.Combine(destination, "Content", "A.txt")));

        Assert.Equal(0, PakCommands.Unpack(new ArgReader([pak.Path, destination, "--overwrite"])));
        Assert.Equal("new-b", File.ReadAllText(existing));
    }

    [Fact]
    public void Unpack_WithAFilter_ChecksOnlyTheEntriesItWouldWrite()
    {
        using var pak = TestPaks.CreateTemp(new Dictionary<string, byte[]>
        {
            ["Content/A.txt"] = Encoding.UTF8.GetBytes("new-a"),
            ["Content/B.txt"] = Encoding.UTF8.GetBytes("new-b"),
        });
        var destination = _folder.Combine("out");
        Directory.CreateDirectory(Path.Combine(destination, "Content"));
        File.WriteAllText(Path.Combine(destination, "Content", "B.txt"), "mine");

        Assert.Equal(0, PakCommands.Unpack(new ArgReader([pak.Path, destination, "--filter", "A.txt"])));

        Assert.Equal("new-a", File.ReadAllText(Path.Combine(destination, "Content", "A.txt")));
    }
}
