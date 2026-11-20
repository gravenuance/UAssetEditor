using System.Text;
using UAssetAPI;
using UAssetEditor.Core.AssetSources;
using UAssetEditor.Core.Games;

namespace UAssetEditor.Core.Tests;

[Collection("Pak")]
public class PakPackerTests
{
    [Fact]
    public void Build_LooseFolder_ProducesAPakContainingEveryFileByRelativePath()
    {
        var sourceFolder = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Pack_" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(sourceFolder, "Sub"));
        File.WriteAllBytes(Path.Combine(sourceFolder, "Foo.uasset"), Encoding.UTF8.GetBytes("foo-uasset"));
        File.WriteAllBytes(Path.Combine(sourceFolder, "Sub", "Bar.uasset"), Encoding.UTF8.GetBytes("bar-uasset"));
        var outputPath = sourceFolder + ".pak";

        try
        {
            PakPacker.Build(sourceFolder, outputPath, cancellationToken: TestContext.Current.CancellationToken);

            using var check = new PakAssetSource(outputPath);
            Assert.Equal(
                new[] { "Foo.uasset", "Sub/Bar.uasset" },
                check.ListAllEntries().OrderBy(e => e, StringComparer.Ordinal));
            using (var rented = check.ReadOriginalBytes("Foo.uasset"))
                Assert.Equal(Encoding.UTF8.GetBytes("foo-uasset"), rented.ToArray());
            using (var rented = check.ReadOriginalBytes("Sub/Bar.uasset"))
                Assert.Equal(Encoding.UTF8.GetBytes("bar-uasset"), rented.ToArray());
        }
        finally
        {
            Directory.Delete(sourceFolder, recursive: true);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void Build_UsesTheGivenMountPoint()
    {
        var sourceFolder = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Pack_" + Guid.NewGuid());
        Directory.CreateDirectory(sourceFolder);
        File.WriteAllBytes(Path.Combine(sourceFolder, "Foo.uasset"), Encoding.UTF8.GetBytes("foo"));
        var outputPath = sourceFolder + ".pak";

        try
        {
            PakPacker.Build(sourceFolder, outputPath, mountPoint: "../../../MyGame/", cancellationToken: TestContext.Current.CancellationToken);

            using var check = new PakAssetSource(outputPath);
            Assert.Equal("../../../MyGame/", check.MountPoint);
        }
        finally
        {
            Directory.Delete(sourceFolder, recursive: true);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void Build_EmptyMountPoint_BakesTheFolderStructureIntoEachEntryInstead()
    {
        // Regression test for the "point at the mod's root folder, no separate mount point"
        // usage pattern - the same result UnrealPak.exe's own -create filelist produces when
        // no -Mount= is passed: the full relative path (here "BendGame/Content/...") ends up
        // stored on every entry itself rather than split out into the pak's header.
        var sourceFolder = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Pack_" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(sourceFolder, "BendGame", "Content"));
        File.WriteAllBytes(Path.Combine(sourceFolder, "BendGame", "Content", "Foo.uasset"), Encoding.UTF8.GetBytes("foo"));
        var outputPath = sourceFolder + ".pak";

        try
        {
            PakPacker.Build(sourceFolder, outputPath, mountPoint: "", cancellationToken: TestContext.Current.CancellationToken);

            using var check = new PakAssetSource(outputPath);
            Assert.Equal("", check.MountPoint);
            Assert.Equal(new[] { "BendGame/Content/Foo.uasset" }, check.ListAllEntries());
        }
        finally
        {
            Directory.Delete(sourceFolder, recursive: true);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void Build_InAGamesVariant_WritesAPakThatGamesReadersOpen()
    {
        var key = PakAesKey.Parse(GameProfile.For(Game.MarvelRivals)!.AesKeyHex);
        var sourceFolder = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Pack_" + Guid.NewGuid());
        Directory.CreateDirectory(sourceFolder);
        File.WriteAllBytes(Path.Combine(sourceFolder, "Foo.uasset"), Encoding.UTF8.GetBytes("foo"));
        var outputPath = sourceFolder + ".pak";

        try
        {
            var result = PakPacker.Build(sourceFolder, outputPath, aesKey: key, variant: PakVariant.MarvelRivals, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(result.HasFailures);
            using var check = new PakAssetSource(outputPath, key, variant: PakVariant.MarvelRivals);
            using var rented = check.ReadOriginalBytes("Foo.uasset");
            Assert.Equal(Encoding.UTF8.GetBytes("foo"), rented.ToArray());
            Assert.ThrowsAny<Exception>(() => PakMountPointReader.Read(outputPath, key, PakVariant.Standard));
        }
        finally
        {
            Directory.Delete(sourceFolder, recursive: true);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }
}
