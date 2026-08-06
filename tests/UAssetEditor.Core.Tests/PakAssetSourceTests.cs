using System.Text;
using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.Core.Tests;

[Collection("Pak")]
public class PakAssetSourceTests
{
    [Fact]
    public void ListAllEntries_ReturnsEveryPackedPath()
    {
        var pakPath = TestPaks.CreatePak(new Dictionary<string, byte[]>
        {
            ["Content/Foo.uasset"] = Encoding.UTF8.GetBytes("a"),
            ["Content/Foo.uexp"] = Encoding.UTF8.GetBytes("b"),
            ["Content/Readme.txt"] = Encoding.UTF8.GetBytes("c"),
        });

        try
        {
            using var source = new PakAssetSource(pakPath);

            Assert.Equal(
                new HashSet<string> { "Content/Foo.uasset", "Content/Foo.uexp", "Content/Readme.txt" },
                source.ListAllEntries().ToHashSet());
        }
        finally
        {
            File.Delete(pakPath);
        }
    }

    [Fact]
    public void EnumerateAssetPaths_ReturnsOnlyUassetEntries()
    {
        var pakPath = TestPaks.CreatePak(new Dictionary<string, byte[]>
        {
            ["Content/Foo.uasset"] = [1],
            ["Content/Foo.uexp"] = [2],
            ["Content/Bar.uasset"] = [3],
        });

        try
        {
            using var source = new PakAssetSource(pakPath);

            Assert.Equal(
                new[] { "Content/Bar.uasset", "Content/Foo.uasset" },
                source.EnumerateAssetPaths().OrderBy(p => p, StringComparer.Ordinal));
        }
        finally
        {
            File.Delete(pakPath);
        }
    }

    [Fact]
    public void SmallPak_EagerlyExtractsUassetEntriesAndCompanionsButNotUnrelatedFiles()
    {
        var uassetBytes = Encoding.UTF8.GetBytes("uasset-content");
        var uexpBytes = Encoding.UTF8.GetBytes("uexp-content");
        var pakPath = TestPaks.CreatePak(new Dictionary<string, byte[]>
        {
            ["Content/Foo.uasset"] = uassetBytes,
            ["Content/Foo.uexp"] = uexpBytes,
            ["Content/Other.txt"] = [9],
        });

        try
        {
            using var source = new PakAssetSource(pakPath); // default threshold => small pak => eager extraction
            Assert.False(source.IsLargePak);

            Assert.True(source.TryGetExtractedPath("Content/Foo.uasset", out var uassetTemp));
            Assert.Equal(uassetBytes, File.ReadAllBytes(uassetTemp));

            Assert.True(source.TryGetExtractedPath("Content/Foo.uexp", out var uexpTemp));
            Assert.Equal(uexpBytes, File.ReadAllBytes(uexpTemp));

            Assert.False(source.TryGetExtractedPath("Content/Other.txt", out _));
        }
        finally
        {
            File.Delete(pakPath);
        }
    }

    [Fact]
    public void LargePak_ExtractsNothingUpFrontOnlyOnDemand()
    {
        var pakPath = TestPaks.CreatePak(new Dictionary<string, byte[]> { ["Content/Foo.uasset"] = [1, 2, 3] });

        try
        {
            using var source = new PakAssetSource(pakPath, largePakThresholdBytes: 1); // force "large" classification
            Assert.True(source.IsLargePak);
            Assert.False(source.TryGetExtractedPath("Content/Foo.uasset", out _));

            var bytes = source.ReadOriginalBytes("Content/Foo.uasset");

            Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
            Assert.False(source.TryGetExtractedPath("Content/Foo.uasset", out _)); // reading original bytes doesn't cache/extract
        }
        finally
        {
            File.Delete(pakPath);
        }
    }

    [Fact]
    public void ExtractEntry_CachesSoASecondCallReturnsTheSamePath()
    {
        var pakPath = TestPaks.CreatePak(new Dictionary<string, byte[]> { ["Content/Foo.uasset"] = [1] });

        try
        {
            using var source = new PakAssetSource(pakPath, largePakThresholdBytes: 1);

            var first = source.ExtractEntry("Content/Foo.uasset");
            var second = source.ExtractEntry("Content/Foo.uasset");

            Assert.Equal(first, second);
        }
        finally
        {
            File.Delete(pakPath);
        }
    }

    [Fact]
    public void Dispose_RemovesTempExtractionDirectory()
    {
        var pakPath = TestPaks.CreatePak(new Dictionary<string, byte[]> { ["Content/Foo.uasset"] = [1] });

        try
        {
            var source = new PakAssetSource(pakPath);
            var tempDir = source.TempExtractionDirectory;
            Assert.True(Directory.Exists(tempDir));

            source.Dispose();

            Assert.False(Directory.Exists(tempDir));
        }
        finally
        {
            File.Delete(pakPath);
        }
    }
}
