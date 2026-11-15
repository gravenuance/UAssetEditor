using UAssetAPI;

namespace UAssetEditor.Core.Tests;

/// <summary>Builds small real .pak files for tests via UAssetAPI's own PakBuilder.Writer, so no binary test fixture needs to be checked in.</summary>
internal static class TestPaks
{
    /// <summary>
    /// Writes a pak into the system temp folder and returns its path; the caller owns deleting it
    /// (or moving it away). Prefer <see cref="CreateTemp"/>, which deletes the file on dispose.
    /// </summary>
    public static string CreatePak(IReadOnlyDictionary<string, byte[]> files, string mountPoint = "../../../Game/", PakVersion version = PakVersion.V11)
    {
        var pakPath = Path.Combine(Path.GetTempPath(), "UAssetEditorTest_" + Guid.NewGuid() + ".pak");

        try
        {
            using var stream = File.Create(pakPath);
            using var builder = new PakBuilder();
            using var writer = builder.Writer(stream, version, mountPoint, 0);
            foreach (var (path, bytes) in files)
                writer.WriteFile(path, bytes);
            writer.WriteIndex();
        }
        catch
        {
            TempPak.DeleteQuietly(pakPath); // a half-written pak must not outlive the failed test
            throw;
        }

        return pakPath;
    }

    /// <summary>
    /// Like <see cref="CreatePak"/>, but the returned <see cref="TempPak"/> deletes the file when disposed.
    /// Declare it before any reader opened on the pak (<c>using var pak = ...; using var source = ...</c>),
    /// so the reader is disposed first and releases the file.
    /// </summary>
    public static TempPak CreateTemp(IReadOnlyDictionary<string, byte[]> files, string mountPoint = "../../../Game/", PakVersion version = PakVersion.V11) =>
        new(CreatePak(files, mountPoint, version));
}

/// <summary>A pak file in the system temp folder that is deleted on dispose, also when the test fails.</summary>
internal sealed class TempPak(string path) : IDisposable
{
    public string Path { get; } = path;

    public void Dispose() => File.Delete(Path); // no-op when already gone; throws if a reader still holds it open, which is a test bug worth seeing

    internal static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
