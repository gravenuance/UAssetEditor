using System.Text;
using UAssetAPI;
using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.Core.Tests;

[Collection("Pak")]
public class PakRepackerTests
{
    [Fact]
    public void Build_DefaultsToTheSourcePaksOwnVersion_NotAHardcodedOne()
    {
        // Regression test: Build used to default to a hardcoded PakVersion.V11 regardless of
        // what version the source pak actually was, which looks structurally fine when
        // re-inspected by this same tool's own reader (repak parses multiple versions
        // generically) but real games are version-aware and can silently fail to recognize
        // a pak claiming the wrong one - reported after a repacked file wasn't picked up by
        // the game despite looking identical in this app's own browser.
        var pakPath = TestPaks.CreatePak(
            new Dictionary<string, byte[]> { ["Content/Foo.uasset"] = Encoding.UTF8.GetBytes("foo") },
            version: PakVersion.V8A);
        var outputPath = pakPath + ".out.pak";

        try
        {
            using (var source = new PakAssetSource(pakPath))
            {
                Assert.Equal(PakVersion.V8A, source.Version); // sanity-check the fixture itself
                PakRepacker.Build(source, outputPath);
            }

            using var check = new PakAssetSource(outputPath);
            Assert.Equal(PakVersion.V8A, check.Version);
        }
        finally
        {
            File.Delete(pakPath);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

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
    public void Build_WithEntryFilter_IncludesOnlyMatchingEntriesAtTheSourcesOwnVersion()
    {
        // Regression test for "Repack Selected": the entryFilter must both narrow the output
        // to just the chosen subset AND still default to the source pak's own version/mount
        // point - a partial pak needs to look exactly like one the same game would produce,
        // same as a full repack already does.
        var files = new Dictionary<string, byte[]>
        {
            ["Content/Keep.uasset"] = Encoding.UTF8.GetBytes("keep"),
            ["Content/Drop.uasset"] = Encoding.UTF8.GetBytes("drop"),
            ["Config/Keep.ini"] = Encoding.UTF8.GetBytes("config"),
        };
        var pakPath = TestPaks.CreatePak(files, mountPoint: "../../../Mod/", version: PakVersion.V8A);
        var outputPath = pakPath + ".out.pak";

        try
        {
            using (var source = new PakAssetSource(pakPath))
                PakRepacker.Build(source, outputPath, entryFilter: entry => entry.StartsWith("Content/Keep", StringComparison.Ordinal) || entry.StartsWith("Config/", StringComparison.Ordinal));

            using var check = new PakAssetSource(outputPath);
            Assert.Equal(
                new[] { "Config/Keep.ini", "Content/Keep.uasset" },
                check.ListAllEntries().OrderBy(k => k, StringComparer.Ordinal));
            Assert.Equal("../../../Mod/", check.MountPoint);
            Assert.Equal(PakVersion.V8A, check.Version);
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
