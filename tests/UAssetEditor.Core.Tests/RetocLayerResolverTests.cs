using UAssetEditor.Core.AssetSources.IoStore;

namespace UAssetEditor.Core.Tests;

/// <summary>
/// Covers the real bug this resolver exists to fix - see its own doc comment: pointing retoc at
/// the enclosing Paks folder (its own directory scan is one level deep only) makes a mod's own
/// container, sitting under "~mods", invisible - so a Paks-folder-wide conversion silently
/// returned the base game's vanilla content for the mod's own entries, confirmed against a real
/// cooked mod this session.
/// </summary>
public class RetocLayerResolverTests
{
    private static (string WorkDir, string PaksFolder, string ModUtoc) CreateFixture()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_RetocLayer_" + Guid.NewGuid());
        var paksFolder = Path.Combine(workDir, "Content", "Paks");
        var modFolder = Path.Combine(paksFolder, "~mods");
        Directory.CreateDirectory(modFolder);

        File.WriteAllText(Path.Combine(paksFolder, "pakchunk0-Windows.utoc"), "base-toc");
        File.WriteAllText(Path.Combine(paksFolder, "pakchunk0-Windows.ucas"), "base-cas");

        var modUtoc = Path.Combine(modFolder, "zMyMod_9999999_P.utoc");
        File.WriteAllText(modUtoc, "mod-toc");
        File.WriteAllText(Path.Combine(modFolder, "zMyMod_9999999_P.ucas"), "mod-cas");

        return (workDir, paksFolder, modUtoc);
    }

    [Fact]
    public void Resolve_WithNoEnclosingPaksFolder_ReturnsTheUtocPathUnchanged()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_RetocLayer_" + Guid.NewGuid());
        Directory.CreateDirectory(workDir);
        var utocPath = Path.Combine(workDir, "standalone.utoc");
        File.WriteAllText(utocPath, "x");
        try
        {
            using var scope = RetocLayerResolver.Resolve(utocPath, RetocLayer.Modded);

            Assert.Equal(utocPath, scope.InputDirectory);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_Original_ReturnsThePaksFolderItself_NoTemporaryDirectory()
    {
        var (workDir, paksFolder, modUtoc) = CreateFixture();
        try
        {
            using var scope = RetocLayerResolver.Resolve(modUtoc, RetocLayer.Original);

            Assert.Equal(Path.GetFullPath(paksFolder), scope.InputDirectory);
            // The mod's own container must NOT be reachable from this directory - Original means vanilla only.
            Assert.False(File.Exists(Path.Combine(scope.InputDirectory, "zMyMod_9999999_P.utoc")));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_Modded_BuildsAScratchDirectoryContainingBothTheBaseGameAndTheModsOwnContainer()
    {
        var (workDir, paksFolder, modUtoc) = CreateFixture();
        try
        {
            using var scope = RetocLayerResolver.Resolve(modUtoc, RetocLayer.Modded);

            Assert.NotEqual(Path.GetFullPath(paksFolder), scope.InputDirectory);
            Assert.Equal("base-toc", File.ReadAllText(Path.Combine(scope.InputDirectory, "pakchunk0-Windows.utoc")));
            Assert.Equal("base-cas", File.ReadAllText(Path.Combine(scope.InputDirectory, "pakchunk0-Windows.ucas")));
            Assert.Equal("mod-toc", File.ReadAllText(Path.Combine(scope.InputDirectory, "zMyMod_9999999_P.utoc")));
            Assert.Equal("mod-cas", File.ReadAllText(Path.Combine(scope.InputDirectory, "zMyMod_9999999_P.ucas")));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void Dispose_RemovesTheScratchDirectory()
    {
        var (workDir, _, modUtoc) = CreateFixture();
        try
        {
            var scope = RetocLayerResolver.Resolve(modUtoc, RetocLayer.Modded);
            var scratchDir = scope.InputDirectory;
            Assert.True(Directory.Exists(scratchDir));

            scope.Dispose();

            Assert.False(Directory.Exists(scratchDir));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void Dispose_AlsoRemovesTheNowEmptySharedWrapperFolder()
    {
        var (workDir, paksFolder, modUtoc) = CreateFixture();
        try
        {
            var scope = RetocLayerResolver.Resolve(modUtoc, RetocLayer.Modded);
            var wrapperDir = Path.GetDirectoryName(scope.InputDirectory)!;
            Assert.Equal(".uae-retoc-temp", Path.GetFileName(wrapperDir));
            Assert.True(Directory.Exists(wrapperDir));

            scope.Dispose();

            Assert.False(Directory.Exists(wrapperDir), "the shared wrapper folder should not be left behind, permanently, in the real Paks folder's parent");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void Dispose_LeavesTheSharedWrapperFolderAloneWhileAnotherScopeStillUsesIt()
    {
        var (workDir, paksFolder, modUtoc) = CreateFixture();
        try
        {
            var scopeA = RetocLayerResolver.Resolve(modUtoc, RetocLayer.Modded);
            var scopeB = RetocLayerResolver.Resolve(modUtoc, RetocLayer.Modded);
            var wrapperDir = Path.GetDirectoryName(scopeA.InputDirectory)!;

            scopeA.Dispose();

            Assert.True(Directory.Exists(wrapperDir), "still in use by scopeB - must not be removed yet");

            scopeB.Dispose();

            Assert.False(Directory.Exists(wrapperDir));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void Dispose_Original_DoesNotDeleteTheRealPaksFolder()
    {
        var (workDir, paksFolder, modUtoc) = CreateFixture();
        try
        {
            var scope = RetocLayerResolver.Resolve(modUtoc, RetocLayer.Original);
            scope.Dispose();

            Assert.True(Directory.Exists(paksFolder));
            Assert.True(File.Exists(Path.Combine(paksFolder, "pakchunk0-Windows.utoc")));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
