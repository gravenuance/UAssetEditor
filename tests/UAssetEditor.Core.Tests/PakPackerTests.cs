using System.Text;
using UAssetEditor.Core.AssetSources;

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
            PakPacker.Build(sourceFolder, outputPath);

            using var check = new PakAssetSource(outputPath);
            Assert.Equal(
                new[] { "Foo.uasset", "Sub/Bar.uasset" },
                check.ListAllEntries().OrderBy(e => e, StringComparer.Ordinal));
            Assert.Equal(Encoding.UTF8.GetBytes("foo-uasset"), check.ReadOriginalBytes("Foo.uasset"));
            Assert.Equal(Encoding.UTF8.GetBytes("bar-uasset"), check.ReadOriginalBytes("Sub/Bar.uasset"));
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
            PakPacker.Build(sourceFolder, outputPath, mountPoint: "../../../MyGame/");

            using var check = new PakAssetSource(outputPath);
            Assert.Equal("../../../MyGame/", check.MountPoint);
        }
        finally
        {
            Directory.Delete(sourceFolder, recursive: true);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }
}
