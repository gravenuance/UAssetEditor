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

    private string SourceFolder()
    {
        var source = _folder.Combine("src");
        Directory.CreateDirectory(Path.Combine(source, "Content"));
        File.WriteAllText(Path.Combine(source, "Content", "A.txt"), "a");
        return source;
    }

    [Fact]
    public void Pack_ToAnExistingPak_RefusesItUnlessOverwriteIsPassed()
    {
        var source = SourceFolder();
        var output = _folder.Combine("mod.pak");
        File.WriteAllText(output, "mine");

        var ex = Assert.Throws<ArgException>(() => PakCommands.Pack(new ArgReader([source, output])));

        Assert.Equal($"--output already exists: {output}. Pass --overwrite to replace it, or choose another path.", ex.Message);
        Assert.Equal("mine", File.ReadAllText(output));
        Assert.Equal(0, PakCommands.Pack(new ArgReader([source, output, "--overwrite"])));
        Assert.NotEqual("mine", File.ReadAllText(output));
    }

    [Theory]
    [InlineData(null, ".ucas")]
    [InlineData("rivals", ".ucas")]
    [InlineData("rivals", ".pak")]
    public void Pack_ToAUtoc_RefusesAnExistingFileOfTheContainer(string? game, string extension)
    {
        var source = SourceFolder();
        var utoc = _folder.Combine("mod_9999999_P.utoc");
        var companion = Path.ChangeExtension(utoc, extension);
        File.WriteAllText(companion, "mine");
        string[] gameArgs = game == null ? [] : ["--game", game];

        var ex = Assert.Throws<ArgException>(() => PakCommands.Pack(new ArgReader([source, utoc, .. gameArgs, "--version", "VER_UE4_27"])));

        Assert.Equal($"{companion} already exists. Pass --overwrite to replace the container's files.", ex.Message);
        Assert.Equal("mine", File.ReadAllText(companion));
    }
    [Fact]
    public void Pack_IntoItsOwnSourceFolder_IsRefused()
    {
        var source = SourceFolder();

        var ex = Assert.Throws<ArgException>(() => PakCommands.Pack(new ArgReader([source, Path.Combine(source, "mod.pak")])));

        Assert.Equal("--output must not be inside the source folder.", ex.Message);
    }

    [Fact]
    public void Repack_ToAnExistingFile_RefusesItUnlessOverwriteIsPassed()
    {
        using var pak = TestPaks.CreateTemp(new Dictionary<string, byte[]> { ["Content/A.txt"] = Encoding.UTF8.GetBytes("a") });
        var output = _folder.Combine("new.pak");
        File.WriteAllText(output, "mine");

        var ex = Assert.Throws<ArgException>(() => PakCommands.Repack(new ArgReader([pak.Path, output])));

        Assert.Equal($"--output already exists: {output}. Pass --overwrite to replace it, or choose another path.", ex.Message);
        Assert.Equal("mine", File.ReadAllText(output));
        Assert.Equal(0, PakCommands.Repack(new ArgReader([pak.Path, output, "--overwrite"])));
        Assert.NotEqual("mine", File.ReadAllText(output));
    }

    [Fact]
    public void Repack_OntoItsOwnSource_IsRefused_EvenWithOverwrite()
    {
        using var pak = TestPaks.CreateTemp(new Dictionary<string, byte[]> { ["Content/A.txt"] = Encoding.UTF8.GetBytes("a") });
        var before = File.ReadAllBytes(pak.Path);
        var hardLink = _folder.Combine("same.pak");
        TestLinks.MakeHardLink(hardLink, pak.Path);

        var ex = Assert.Throws<ArgException>(() => PakCommands.Repack(new ArgReader([pak.Path, hardLink, "--overwrite"])));

        Assert.Equal("the pak and --output are the same file.", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(pak.Path));
        File.Delete(hardLink);
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
