using System.Text;
using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.Core.Tests;

[Collection("Pak")]
public class PakRepackerTests
{
    [Fact]
    public void Build_UntouchedSmallPak_ProducesIdenticalEntryContent()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["Content/Foo.uasset"] = Encoding.UTF8.GetBytes("foo-uasset"),
            ["Content/Foo.uexp"] = Encoding.UTF8.GetBytes("foo-uexp"),
            ["Content/Bar.uasset"] = Encoding.UTF8.GetBytes("bar-uasset"),
        };
        var pakPath = TestPaks.CreatePak(files);
        var outputPath = pakPath + ".out.pak";

        try
        {
            using (var source = new PakAssetSource(pakPath))
                PakRepacker.Build(source, outputPath);

            using var check = new PakAssetSource(outputPath);
            Assert.Equal(files.Keys.OrderBy(k => k, StringComparer.Ordinal), check.ListAllEntries().OrderBy(k => k, StringComparer.Ordinal));
            foreach (var (path, expectedBytes) in files)
                Assert.Equal(expectedBytes, check.ReadOriginalBytes(path));
        }
        finally
        {
            File.Delete(pakPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Fact]
    public void Build_LargePakWithOnlyOneEntryTouched_KeepsUntouchedEntriesAndIncludesTheEdit()
    {
        var originalBytes = Encoding.UTF8.GetBytes("original");
        var editedBytes = Encoding.UTF8.GetBytes("edited-content");
        var untouchedBytes = Encoding.UTF8.GetBytes("untouched");

        var pakPath = TestPaks.CreatePak(new Dictionary<string, byte[]>
        {
            ["Content/Edited.uasset"] = originalBytes,
            ["Content/Untouched.uasset"] = untouchedBytes,
        });
        var outputPath = pakPath + ".out.pak";

        try
        {
            using (var source = new PakAssetSource(pakPath, largePakThresholdBytes: 1)) // force lazy mode
            {
                Assert.True(source.IsLargePak);
                Assert.False(source.TryGetExtractedPath("Content/Edited.uasset", out _));
                Assert.False(source.TryGetExtractedPath("Content/Untouched.uasset", out _));

                // Simulate "the user opened this entry, edited it, and saved" - OpenAsset/SaveAsset
                // both funnel through ExtractEntry for the temp copy; writing to it here stands in
                // for what UAsset.Write(tempPath) would have done, without needing real UAssetAPI-
                // parseable content for this test.
                var tempPath = source.ExtractEntry("Content/Edited.uasset");
                File.WriteAllBytes(tempPath, editedBytes);

                PakRepacker.Build(source, outputPath);
            }

            using var check = new PakAssetSource(outputPath, largePakThresholdBytes: long.MaxValue);
            Assert.Equal(editedBytes, check.ReadOriginalBytes("Content/Edited.uasset"));
            Assert.Equal(untouchedBytes, check.ReadOriginalBytes("Content/Untouched.uasset"));
        }
        finally
        {
            File.Delete(pakPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }
}
