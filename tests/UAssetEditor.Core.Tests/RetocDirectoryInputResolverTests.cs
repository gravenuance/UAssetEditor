using UAssetEditor.Core.AssetSources.IoStore;

namespace UAssetEditor.Core.Tests;

/// <summary>
/// Covers the real-world bug this resolver exists to route around - see its own doc comment
/// and <see cref="MainViewModel"/>/<see cref="PackFolderViewModel"/>'s use of it (App project,
/// not referenced here) - confirmed against the real vendored retoc.exe: pointing `to-zen`
/// directly at a folder that already has Content/ inside it produces
/// "Failed to get Package Path from Content/...", while pointing it at that folder's parent
/// does not.
/// </summary>
public class RetocDirectoryInputResolverTests
{
    [Fact]
    public void Resolve_OnAFolderThatIsTheProjectRoot_ReturnsItsParent()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Retoc_" + Guid.NewGuid());
        var projectFolder = Path.Combine(workDir, "SB");
        Directory.CreateDirectory(Path.Combine(projectFolder, "Content"));
        try
        {
            var resolved = RetocDirectoryInputResolver.Resolve(projectFolder);

            Assert.Equal(Path.GetFullPath(workDir), resolved);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("Config")]
    [InlineData("Plugins")]
    [InlineData("Movies")]
    public void Resolve_RecognizesOtherProjectRootMarkersBesidesContent(string marker)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Retoc_" + Guid.NewGuid());
        var projectFolder = Path.Combine(workDir, "SB");
        Directory.CreateDirectory(Path.Combine(projectFolder, marker));
        try
        {
            var resolved = RetocDirectoryInputResolver.Resolve(projectFolder);

            Assert.Equal(Path.GetFullPath(workDir), resolved);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_OnAFolderThatAlreadyWrapsTheProjectRoot_ReturnsItUnchanged()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Retoc_" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(workDir, "SB", "Content"));
        try
        {
            var resolved = RetocDirectoryInputResolver.Resolve(workDir);

            Assert.Equal(workDir.TrimEnd('\\', '/'), resolved);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("../../../SB/", "SB")]
    [InlineData("../../../SB", "SB")]
    [InlineData("../../../StellarBlade/Content/", "StellarBlade")]
    [InlineData("../../../Game/", "Game")]
    public void ExtractProjectName_PullsTheSegmentAfterTheDotDotRun(string mountPoint, string expected)
    {
        Assert.Equal(expected, RetocDirectoryInputResolver.ExtractProjectName(mountPoint));
    }

    [Fact]
    public void ExtractProjectName_OnAMountPointWithNoSegmentAfterDotDot_ReturnsNull()
    {
        Assert.Null(RetocDirectoryInputResolver.ExtractProjectName("../../../"));
    }
}
