using UAssetEditor.Core.AssetSources.IoStore;

namespace UAssetEditor.Core.Tests;

/// <summary>
/// Covers the real-world scenario this resolver exists to route around - see its own doc
/// comment and <see cref="MainViewModel"/>/<see cref="ConvertIoStoreToLegacyViewModel"/>'s use of
/// it (App project, not referenced here): a single-mod .utoc that only overrides a few of a
/// character's assets can't resolve imports into the base game's own separate container on its
/// own, but retoc can when pointed at the whole enclosing Paks folder instead - confirmed against
/// a real cooked container.
/// </summary>
public class RetocPaksFolderResolverTests
{
    [Fact]
    public void FindEnclosingPaksFolder_OnAModNestedUnderPaks_FindsIt()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Retoc_" + Guid.NewGuid());
        var paksFolder = Path.Combine(workDir, "Content", "Paks");
        var modFolder = Path.Combine(paksFolder, "~mods", "someauthor");
        Directory.CreateDirectory(modFolder);
        var utocPath = Path.Combine(modFolder, "mod.utoc");
        try
        {
            var resolved = RetocPaksFolderResolver.FindEnclosingPaksFolder(utocPath);

            Assert.Equal(Path.GetFullPath(paksFolder), resolved);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void FindEnclosingPaksFolder_MatchesCaseInsensitively()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Retoc_" + Guid.NewGuid());
        var paksFolder = Path.Combine(workDir, "Content", "PAKS");
        Directory.CreateDirectory(paksFolder);
        var utocPath = Path.Combine(paksFolder, "container.utoc");
        try
        {
            var resolved = RetocPaksFolderResolver.FindEnclosingPaksFolder(utocPath);

            Assert.Equal(Path.GetFullPath(paksFolder), resolved);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void FindEnclosingPaksFolder_WithNoPaksAncestor_ReturnsNull()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Retoc_" + Guid.NewGuid());
        Directory.CreateDirectory(workDir);
        var utocPath = Path.Combine(workDir, "container.utoc");
        try
        {
            var resolved = RetocPaksFolderResolver.FindEnclosingPaksFolder(utocPath);

            Assert.Null(resolved);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void FindEnclosingPaksFolder_WithMultiplePaksAncestors_ReturnsTheNearestOne()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Retoc_" + Guid.NewGuid());
        var outerPaks = Path.Combine(workDir, "Paks");
        var innerPaks = Path.Combine(outerPaks, "Nested", "Paks");
        Directory.CreateDirectory(innerPaks);
        var utocPath = Path.Combine(innerPaks, "container.utoc");
        try
        {
            var resolved = RetocPaksFolderResolver.FindEnclosingPaksFolder(utocPath);

            Assert.Equal(Path.GetFullPath(innerPaks), resolved);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
