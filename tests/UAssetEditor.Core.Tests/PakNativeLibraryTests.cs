using System.IO.Compression;
using UAssetAPI;

namespace UAssetEditor.Core.Tests;

public sealed class PakNativeLibraryTests
{
    private static byte[] EmbeddedWindowsLibrary()
    {
        using var resource = typeof(PakBuilder).Assembly.GetManifestResourceStream("UAssetAPI.repak_bind.dll.gz")
            ?? throw new InvalidOperationException("UAssetAPI embeds no repak_bind.dll.gz");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        using var bytes = new MemoryStream();
        gzip.CopyTo(bytes);
        return bytes.ToArray();
    }

    [Fact]
    public void TheEmbeddedLibrary_NeverSearchesForOodleByName()
    {
        var library = EmbeddedWindowsLibrary();

        // A build that falls back to oo2core_9_win64.dll beside the executable or on PATH carries that name;
        // the host's validated REPAK_OODLE_DLL must be the only way an Oodle DLL loads.
        Assert.True(library.AsSpan().IndexOf("oo2core"u8) < 0, "the embedded repak_bind.dll names an Oodle DLL to search for");
        Assert.True(library.AsSpan().IndexOf("REPAK_OODLE_DLL"u8) >= 0, "the embedded repak_bind.dll does not read REPAK_OODLE_DLL");
    }
}
