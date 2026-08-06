using UAssetAPI;

namespace UAssetEditor.Core.Tests;

/// <summary>Builds small real .pak files for tests via UAssetAPI's own PakBuilder.Writer, so no binary test fixture needs to be checked in.</summary>
internal static class TestPaks
{
    public static string CreatePak(IReadOnlyDictionary<string, byte[]> files, string mountPoint = "../../../Game/")
    {
        var pakPath = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_" + Guid.NewGuid() + ".pak");

        using (var stream = File.Create(pakPath))
        {
            var writer = new PakBuilder().Writer(stream, PakVersion.V11, mountPoint, 0);
            foreach (var (path, bytes) in files)
                writer.WriteFile(path, bytes);
            writer.WriteIndex();
        }

        return pakPath;
    }
}
