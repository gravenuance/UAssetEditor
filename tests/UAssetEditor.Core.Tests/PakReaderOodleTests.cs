using UAssetAPI;
using UAssetEditor.Core.AssetSources.PakWorker;

namespace UAssetEditor.Core.Tests;

/// <summary>Reads an Oodle-marked pak through the real worker without any Oodle DLL: the DLL handed over never exists.</summary>
[Collection("Pak")]
public class PakReaderOodleTests
{
    [Fact]
    public async Task ReadEntry_OfAnOodlePakWhoseDllCannotBeLoaded_NamesTheDllAndTheFix()
    {
        var absentDll = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_NoOodle_" + Guid.NewGuid().ToString("N"), "oo2core_9_win64.dll");
        using var pak = new TempPak(CreateOodleMarkedPak());
        using var reader = new PakReaderHandle(PakWorkerProcess.Shared, pak.Path, null, _ => new OodleLookup(absentDll));
        await reader.OpenAsync(TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadEntryAsync("Foo.uasset", TestContext.Current.CancellationToken));

        Assert.Equal($"Could not load {absentDll}. Use a 64-bit oo2core_*_win64.dll.", ex.Message);
    }

    /// <summary>repak cannot write Oodle, so this writes a Zlib pak and renames the codec in its footer: reading the entry then needs Oodle.</summary>
    private static string CreateOodleMarkedPak()
    {
        var path = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_" + Guid.NewGuid() + ".pak");
        using (var stream = File.Create(path))
        using (var builder = new PakBuilder())
        {
            builder.Compression([PakCompression.Zlib]);
            using var writer = builder.Writer(stream, PakVersion.V11, "../../../Game/", 0);
            writer.WriteFile("Foo.uasset", new byte[4096]);
            writer.WriteIndex();
        }

        var bytes = File.ReadAllBytes(path);
        var footer = bytes.AsSpan(bytes.Length - 512);
        var at = footer.IndexOf("Zlib"u8);
        Assert.True(at >= 0, "the pak footer does not name its Zlib codec");
        "Oodle"u8.CopyTo(footer[at..]);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
