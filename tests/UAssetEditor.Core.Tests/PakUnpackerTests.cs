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
                PakUnpacker.Unpack(source, destination, cancellationToken: TestContext.Current.CancellationToken);

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
                PakUnpacker.Unpack(source, destination, progress: progress, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal((1, 2), progress.Values[0]);
            Assert.Equal((2, 2), progress.Values[1]);
        }
        finally
        {
            File.Delete(pakPath);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public void Unpack_WithEntryFilter_ExtractsOnlyMatchingEntries()
    {
        // The filter is what the Browse tree's "Extract Selected" (checked folders/assets)
        // is built on - a subtree prefix check like this one is exactly that use case.
        var pakPath = TestPaks.CreatePak(new Dictionary<string, byte[]>
        {
            ["Content/Weapons/Rifle.uasset"] = Encoding.UTF8.GetBytes("rifle"),
            ["Content/Weapons/Pistol.uasset"] = Encoding.UTF8.GetBytes("pistol"),
            ["Content/Vehicles/Car.uasset"] = Encoding.UTF8.GetBytes("car"),
        });
        var destination = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Unpack_" + Guid.NewGuid());

        try
        {
            using var source = new PakAssetSource(pakPath);
            var result = PakUnpacker.Unpack(source, destination, entryFilter: e => e.StartsWith("Content/Weapons/", StringComparison.Ordinal), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, result.SucceededCount);
            Assert.Empty(result.FailedEntries);
            Assert.True(File.Exists(Path.Combine(destination, "Content", "Weapons", "Rifle.uasset")));
            Assert.True(File.Exists(Path.Combine(destination, "Content", "Weapons", "Pistol.uasset")));
            Assert.False(File.Exists(Path.Combine(destination, "Content", "Vehicles", "Car.uasset")));
        }
        finally
        {
            File.Delete(pakPath);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public void Unpack_EntryPathEscapesDestinationFolder_FailsThatEntryInsteadOfWritingOutsideIt()
    {
        // Security regression: pak entry paths are untrusted external input. Confirmed against
        // the real repak writer/reader that an entry literally named with a "../" traversal
        // sequence round-trips completely unsanitized - nothing upstream of PakUnpacker stops
        // a hand-crafted (or malicious) pak from naming an entry this way.
        var outsideMarker = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Escaped_" + Guid.NewGuid() + ".txt");
        var pakPath = TestPaks.CreatePak(new Dictionary<string, byte[]>
        {
            [$"../{Path.GetFileName(outsideMarker)}"] = Encoding.UTF8.GetBytes("pwned"),
            ["Content/Foo.uasset"] = Encoding.UTF8.GetBytes("foo"),
        });
        var destination = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_Unpack_" + Guid.NewGuid());

        try
        {
            using var source = new PakAssetSource(pakPath);
            var result = PakUnpacker.Unpack(source, destination, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, result.SucceededCount);
            var failedEntry = Assert.Single(result.FailedEntries);
            Assert.Contains("escapes the destination folder", failedEntry.Reason, StringComparison.Ordinal);
            Assert.False(File.Exists(outsideMarker), "The traversal entry must not have been written outside the destination folder.");
            Assert.True(File.Exists(Path.Combine(destination, "Content", "Foo.uasset")));
        }
        finally
        {
            File.Delete(pakPath);
            if (File.Exists(outsideMarker)) File.Delete(outsideMarker);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public void Unpack_WhenAFolderInsideTheDestinationIsAJunction_FailsThoseEntriesInsteadOfWritingThroughIt()
    {
        // A junction already inside the destination (planted, or left by an earlier extraction) would send the entry's
        // folder, and the file in it, to wherever the junction points. A lexical prefix check cannot see that.
        if (!OperatingSystem.IsWindows()) return;
        var pakPath = TestPaks.CreatePak(new Dictionary<string, byte[]>
        {
            ["Content/Foo.uasset"] = Encoding.UTF8.GetBytes("foo"),
            ["Content/Deeper/Bar.uasset"] = Encoding.UTF8.GetBytes("bar"),
            ["Other/Baz.uasset"] = Encoding.UTF8.GetBytes("baz"),
        });
        var root = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_UnpackLinks_" + Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(root, "dest");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(outside);
        using var links = new TestLinks();
        links.MakeJunction(Path.Combine(destination, "Content"), outside);

        try
        {
            using var source = new PakAssetSource(pakPath);
            var result = PakUnpacker.Unpack(source, destination, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, result.SucceededCount);
            Assert.Equal(["Content/Deeper/Bar.uasset", "Content/Foo.uasset"], result.FailedEntries.Select(f => f.Entry).Order(StringComparer.Ordinal));
            Assert.All(result.FailedEntries, f => Assert.Contains("link inside the destination folder", f.Reason, StringComparison.Ordinal));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
            Assert.True(File.Exists(Path.Combine(destination, "Other", "Baz.uasset")));
        }
        finally
        {
            File.Delete(pakPath);
            links.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Unpack_WhenTheDestinationItselfIsReachedThroughAJunction_StillWritesThere()
    {
        // The caller chose that folder, links and all; only a link met below it is suspect.
        if (!OperatingSystem.IsWindows()) return;
        var pakPath = TestPaks.CreatePak(new Dictionary<string, byte[]> { ["Content/Foo.uasset"] = Encoding.UTF8.GetBytes("foo") });
        var root = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_UnpackLinks_" + Guid.NewGuid().ToString("N"));
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);
        using var links = new TestLinks();
        links.MakeJunction(Path.Combine(root, "alias"), real);

        try
        {
            using var source = new PakAssetSource(pakPath);
            var result = PakUnpacker.Unpack(source, Path.Combine(root, "alias", "dest"), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, result.SucceededCount);
            Assert.Empty(result.FailedEntries);
            Assert.True(File.Exists(Path.Combine(real, "dest", "Content", "Foo.uasset")));
        }
        finally
        {
            File.Delete(pakPath);
            links.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
