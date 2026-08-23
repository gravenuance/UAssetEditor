using System.Text;
using UAssetEditor.Core.AssetSources;

namespace UAssetEditor.Core.Tests;

[Collection("Pak")]
public class PakUnpackerTests
{
    /// <summary>Reports synchronously on the calling thread - unlike <see cref="Progress{T}"/>, which posts through a captured SynchronizationContext (or the thread pool, absent one), so asserting against it right after the call returns would be a race.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = new();
        public void Report(T value) => Values.Add(value);
    }

    [Fact]
    public void Unpack_WritesEveryEntryUnderTheDestinationFolder_MirroringInternalPaths()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["Content/Foo.uasset"] = Encoding.UTF8.GetBytes("foo-uasset"),
            ["Content/Foo.uexp"] = Encoding.UTF8.GetBytes("foo-uexp"),
            ["Content/Sub/Bar.uasset"] = Encoding.UTF8.GetBytes("bar-uasset"),
        };
        var pakPath = TestPaks.CreatePak(files);
        var destination = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Unpack_" + Guid.NewGuid());

        try
        {
            using (var source = new PakAssetSource(pakPath))
                PakUnpacker.Unpack(source, destination);

            foreach (var (path, expectedBytes) in files)
            {
                var onDisk = Path.Combine(destination, path.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(onDisk), $"Expected {onDisk} to exist.");
                Assert.Equal(expectedBytes, File.ReadAllBytes(onDisk));
            }
        }
        finally
        {
            File.Delete(pakPath);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public void Unpack_ReportsProgressForEveryEntry()
    {
        var pakPath = TestPaks.CreatePak(new Dictionary<string, byte[]>
        {
            ["Content/A.uasset"] = Encoding.UTF8.GetBytes("a"),
            ["Content/B.uasset"] = Encoding.UTF8.GetBytes("b"),
        });
        var destination = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Unpack_" + Guid.NewGuid());
        var progress = new SyncProgress<(int Done, int Total)>();

        try
        {
            using (var source = new PakAssetSource(pakPath))
                PakUnpacker.Unpack(source, destination, progress);

            Assert.Equal((1, 2), progress.Values[0]);
            Assert.Equal((2, 2), progress.Values[1]);
        }
        finally
        {
            File.Delete(pakPath);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }
}
